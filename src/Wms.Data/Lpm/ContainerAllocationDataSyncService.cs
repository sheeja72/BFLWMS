using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Copies a Container Allocation's detail rows (LPMSIM.dbo.WMS_ContAllocationData
/// for an approved batch) to either:
///   • the Azure WMS DB (mirror table dbo.WMS_ContAllocationData), or
///   • the on-prem WmsProductionDb (legacy table online.dbo.PhotoCheckingResult).
///
/// One sync per ContNo, full stop — once any prior sync exists for this ContNo
/// in WMS_ContAllocationDataSync_Log (regardless of destination, status, or
/// BatchNo), no further sync is allowed (per spec Q4).
///
/// Activity is logged to Azure WMS DB.WMS_ContAllocationDataSync_Log.
/// </summary>
public class ContainerAllocationDataSyncService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 300;

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenWmsProductionDb()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsProductionDbConnectionString()));
        c.Open();
        return c;
    }

    // ===================== Read-side =====================

    /// <summary>Approved containers, newest approval first. Joins the sync log to
    /// flag containers that have already been synced (per Q4 — any prior sync
    /// blocks the row).</summary>
    public async Task<List<ApprovedContnoRow>> GetApprovedContnosAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        // Approved Headers + their detail-row counts come from LPMSIM. The
        // sync-log lookup happens on Azure WMS, so we two-step: get the LPMSIM
        // rows first, then mark already-synced via the log.
        var rows = (await c.QueryAsync<(string ContNo, int BatchCount, int TotalAllocatedQty, DateTime LatestApprovedDt)>(new CommandDefinition(@"
            SELECT h.ContNo,
                   COUNT(DISTINCT h.BatchNo)                  AS BatchCount,
                   ISNULL(SUM(h.TotalQty), 0)                 AS TotalAllocatedQty,
                   MAX(h.ApprovedDt)                          AS LatestApprovedDt
              FROM LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK)
             WHERE h.ApprovedDt IS NOT NULL
             GROUP BY h.ContNo
             ORDER BY MAX(h.ApprovedDt) DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).AsList();

        if (rows.Count == 0) return new();

        HashSet<string> synced;
        await using (var w = OpenWms())
        {
            var contnos = rows.Select(r => r.ContNo).Distinct().ToArray();
            synced = (await w.QueryAsync<string>(new CommandDefinition(@"
                SELECT DISTINCT ContNo
                  FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
                 WHERE ContNo IN @cs",
                new { cs = contnos }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return rows.Select(r => new ApprovedContnoRow(
            r.ContNo, r.BatchCount, r.TotalAllocatedQty, r.LatestApprovedDt,
            synced.Contains(r.ContNo))).ToList();
    }

    /// <summary>Last N rows from the sync log. Optional ContNo substring filter
    /// (so users can search "is AELOC… already synced?").</summary>
    public async Task<List<DataSyncActivityRow>> GetRecentActivityAsync(
        int top = 50, string? searchContno = null, CancellationToken ct = default)
    {
        var like = string.IsNullOrWhiteSpace(searchContno) ? null : "%" + searchContno.Trim() + "%";
        await using var c = OpenWms();
        var rows = await c.QueryAsync<DataSyncActivityRow>(new CommandDefinition($@"
            SELECT TOP ({top})
                   SyncId, ContNo, BatchNo, Destination, TotalAllocatedQty,
                   Status, ErrorMessage, SyncedBy, SyncedTS
              FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
             WHERE (@s IS NULL OR ContNo LIKE @s)
             ORDER BY SyncedTS DESC, SyncId DESC",
            new { s = like }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>True if this ContNo has any sync log entry (Q4 gate).</summary>
    public async Task<bool> IsAlreadySyncedAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return false;
        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 1 FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK) WHERE ContNo = @c",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return hit == 1;
    }

    // ===================== Write-side =====================

    /// <summary>Sync entry point — validates the gate, dispatches to the
    /// destination-specific copy method, and writes the log row.</summary>
    public async Task<DataSyncResult> SyncAsync(string contno, DataSyncDestination destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno))
            return new DataSyncResult(false, "Container number is required.", null, 0);
        contno = contno.Trim();

        // Q4 gate: any prior sync for this ContNo blocks.
        if (await IsAlreadySyncedAsync(contno, ct))
            return new DataSyncResult(false,
                $"Container {contno} has already been synced. One sync per container is the limit — see the Recent Activity table for who synced it and when.",
                null, 0);

        // Source rows from LPMSIM — ALL approved batches for this ContNo. If a
        // container has both FillSKUMax + RoundRobin approved, both ship.
        List<SourceRow> sourceRows;
        int? primaryBatchNo;
        int totalAllocatedQty;
        await using (var src = OpenOnPremBackup())
        {
            sourceRows = (await src.QueryAsync<SourceRow>(new CommandDefinition(@"
                SELECT d.BatchNo,
                       d.ContNo, d.Country, d.TrnDate, d.Time1, d.UPC, d.Itemcode, d.Barcode,
                       d.GroupCode, d.Qty, d.SkuMax, d.AllocatedQty, d.PrevAllocatedQty, d.QtyIssue,
                       d.StoreID, d.TcmContno, d.Itemname, d.BuildingCategory, d.LPMDt, d.LPMBoxNO,
                       d.ORAPONo, d.Division, d.Brand, d.DivCode, d.Department, d.Season, d.Style,
                       [Size] = d.Size,
                       d.SalesPrice, d.ResultType, d.FinalResult, d.Result, d.Remarks, d.OTS,
                       Color    = u.color,
                       Gender   = u.GENDER,
                       HsCode   = u.hscode,
                       [Class]  = s.[Class],
                       Family   = s.Family,
                       Subclass = s.Subclass
                  FROM LPMSIM.dbo.WMS_ContAllocationData d WITH (NOLOCK)
                  JOIN LPMSIM.dbo.WMS_Cont_Allocation_Header h WITH (NOLOCK) ON h.BatchNo = d.BatchNo
                  OUTER APPLY (
                       SELECT TOP 1 uo.color, uo.GENDER, uo.hscode
                         FROM usa.dbo.usaorgfile uo WITH (NOLOCK)
                        WHERE uo.ContNo = d.ContNo AND uo.ItemCode = d.Itemcode
                        ORDER BY uo.TrnDate DESC
                  ) u
                  OUTER APPLY (
                       SELECT TOP 1 sm.[Class], sm.Family, sm.Subclass
                         FROM datareporting.dbo.vupc_subclass v WITH (NOLOCK)
                         LEFT JOIN datareporting.dbo.SubclassMaster sm WITH (NOLOCK) ON sm.MH4ID = v.MH4ID
                        WHERE v.itemcode = d.Itemcode
                  ) s
                 WHERE h.ContNo = @c AND h.ApprovedDt IS NOT NULL
                 ORDER BY h.BatchNo, d.IdNo",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();

            primaryBatchNo    = sourceRows.FirstOrDefault()?.BatchNo;
            totalAllocatedQty = sourceRows.Sum(r => r.AllocatedQty ?? r.Qty ?? 0);
        }

        if (sourceRows.Count == 0)
            return new DataSyncResult(false, $"No approved allocation rows found for container {contno}.", null, 0);

        // Dispatch.
        string? error = null;
        int rowsCopied = 0;
        try
        {
            rowsCopied = destination switch
            {
                DataSyncDestination.AzureWmsDb       => await CopyToAzureWmsAsync(sourceRows, ct),
                DataSyncDestination.WmsProductionDb  => await CopyToWmsProductionDbAsync(sourceRows, ct),
                _ => throw new InvalidOperationException($"Unknown destination: {destination}"),
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // Log the attempt (success or failure).
        var syncId = await WriteLogRowAsync(
            contno, primaryBatchNo, destination, totalAllocatedQty,
            status: error is null ? "Success" : "Failed", error, ct);

        return error is null
            ? new DataSyncResult(true,
                $"Synced {rowsCopied:N0} rows for {contno} to {DestinationLabel(destination)}.",
                syncId, rowsCopied)
            : new DataSyncResult(false,
                $"Sync to {DestinationLabel(destination)} failed: {error}",
                syncId, rowsCopied);
    }

    // ----- destination writers -----

    private async Task<int> CopyToAzureWmsAsync(List<SourceRow> rows, CancellationToken ct)
    {
        // Mirror table — straight column-for-column SqlBulkCopy from SourceRow.
        var dt = BuildAzureMirrorDataTable(rows);
        await using var conn = OpenWms();
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "dbo.WMS_ContAllocationData",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
        return rows.Count;
    }

    private async Task<int> CopyToWmsProductionDbAsync(List<SourceRow> rows, CancellationToken ct)
    {
        // online.dbo.PhotoCheckingResult — explicit column mapping. Fields not on
        // the source side (Result, QtyIssuedResult, ShopCode, OrPrice, …) are left
        // unset; SQL Server defaults / NULL apply.
        var dt = BuildPhotoCheckingResultDataTable(rows);
        await using var conn = OpenWmsProductionDb();
        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = "online.dbo.PhotoCheckingResult",
            BatchSize            = 1000,
            BulkCopyTimeout      = CommandTimeoutSeconds,
        };
        foreach (System.Data.DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
        return rows.Count;
    }

    private async Task<int?> WriteLogRowAsync(string contno, int? batchNo, DataSyncDestination dest,
        int? totalAllocatedQty, string status, string? error, CancellationToken ct)
    {
        try
        {
            await using var c = OpenWms();
            return await c.ExecuteScalarAsync<int?>(new CommandDefinition(@"
                INSERT INTO dbo.WMS_ContAllocationDataSync_Log
                    (ContNo, BatchNo, Destination, TotalAllocatedQty, Status, ErrorMessage, SyncedBy)
                OUTPUT INSERTED.SyncId
                VALUES (@c, @b, @d, @q, @s, @e, @u)",
                new { c = contno, b = batchNo, d = dest.ToString(),
                      q = totalAllocatedQty, s = status, e = error, u = user.Name },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }
        catch
        {
            // The log INSERT shouldn't itself drop the sync result — swallow.
            return null;
        }
    }

    // ===================== DataTable builders =====================

    private static System.Data.DataTable BuildAzureMirrorDataTable(List<SourceRow> rows)
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("BatchNo",          typeof(int));
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("Country",          typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("SkuMax",           typeof(int));
        dt.Columns.Add("AllocatedQty",     typeof(int));
        dt.Columns.Add("PrevAllocatedQty", typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("StoreID",          typeof(string));
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("LPMBoxNO",         typeof(string));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("Brand",            typeof(string));
        dt.Columns.Add("DivCode",          typeof(int));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Size",             typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(decimal));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("Result",           typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("OTS",              typeof(double));
        dt.Columns.Add("Color",            typeof(string));
        dt.Columns.Add("Gender",           typeof(string));
        dt.Columns.Add("HsCode",           typeof(string));
        dt.Columns.Add("Class",            typeof(string));
        dt.Columns.Add("Family",           typeof(string));
        dt.Columns.Add("Subclass",         typeof(string));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                (object?)r.BatchNo          ?? DBNull.Value,
                (object?)r.ContNo           ?? DBNull.Value,
                (object?)r.Country          ?? DBNull.Value,
                (object?)r.TrnDate          ?? DBNull.Value,
                (object?)r.Time1            ?? DBNull.Value,
                (object?)r.UPC              ?? DBNull.Value,
                (object?)r.Itemcode         ?? DBNull.Value,
                (object?)r.Barcode          ?? DBNull.Value,
                (object?)r.GroupCode        ?? DBNull.Value,
                (object?)r.Qty              ?? DBNull.Value,
                (object?)r.SkuMax           ?? DBNull.Value,
                (object?)r.AllocatedQty     ?? DBNull.Value,
                (object?)r.PrevAllocatedQty ?? DBNull.Value,
                (object?)r.QtyIssue         ?? DBNull.Value,
                (object?)r.StoreID          ?? DBNull.Value,
                (object?)r.TcmContno        ?? DBNull.Value,
                (object?)r.Itemname         ?? DBNull.Value,
                (object?)r.BuildingCategory ?? DBNull.Value,
                (object?)r.LPMDt            ?? DBNull.Value,
                (object?)r.LPMBoxNO         ?? DBNull.Value,
                (object?)r.ORAPONo          ?? DBNull.Value,
                (object?)r.Division         ?? DBNull.Value,
                (object?)r.Brand            ?? DBNull.Value,
                (object?)r.DivCode          ?? DBNull.Value,
                (object?)r.Department       ?? DBNull.Value,
                (object?)r.Season           ?? DBNull.Value,
                (object?)r.Style            ?? DBNull.Value,
                (object?)r.Size             ?? DBNull.Value,
                (object?)r.SalesPrice       ?? DBNull.Value,
                (object?)r.ResultType       ?? DBNull.Value,
                (object?)r.FinalResult      ?? DBNull.Value,
                (object?)r.Result           ?? DBNull.Value,
                (object?)r.Remarks          ?? DBNull.Value,
                (object?)r.OTS              ?? DBNull.Value,
                (object?)r.Color            ?? DBNull.Value,
                (object?)r.Gender           ?? DBNull.Value,
                (object?)r.HsCode           ?? DBNull.Value,
                (object?)r.Class            ?? DBNull.Value,
                (object?)r.Family           ?? DBNull.Value,
                (object?)r.Subclass         ?? DBNull.Value);
        }
        return dt;
    }

    private static System.Data.DataTable BuildPhotoCheckingResultDataTable(List<SourceRow> rows)
    {
        // PhotoCheckingResult column subset that we have source data for. Columns
        // we don't populate (Result, QtyIssuedResult, ShopCode, OrPrice, PrintFlag,
        // RfidFlag, Company, RefNo, Mark, Uid, RStatus, RDateTime, PStatus,
        // PDateTime, Excess) are skipped — destination defaults/NULL apply.
        var dt = new System.Data.DataTable();
        dt.Columns.Add("ContNo",           typeof(string));
        dt.Columns.Add("TrnDate",          typeof(DateTime));
        dt.Columns.Add("Time1",            typeof(TimeSpan));
        dt.Columns.Add("UPC",              typeof(string));
        dt.Columns.Add("Itemcode",         typeof(string));
        dt.Columns.Add("GroupCode",        typeof(string));
        dt.Columns.Add("Season",           typeof(string));
        dt.Columns.Add("Department",       typeof(string));
        dt.Columns.Add("Division",         typeof(string));
        dt.Columns.Add("FinalResult",      typeof(string));
        dt.Columns.Add("ResultType",       typeof(string));
        dt.Columns.Add("Qty",              typeof(int));
        dt.Columns.Add("QtyIssue",         typeof(int));
        dt.Columns.Add("Itemname",         typeof(string));
        dt.Columns.Add("Barcode",          typeof(string));
        dt.Columns.Add("SalesPrice",       typeof(string));   // varchar(30) on PhotoCheckingResult
        dt.Columns.Add("TcmContno",        typeof(string));
        dt.Columns.Add("BuildingCategory", typeof(string));
        dt.Columns.Add("LPMDt",            typeof(DateTime));
        dt.Columns.Add("LPMBoxNO",         typeof(string));
        dt.Columns.Add("ORAPONo",          typeof(string));
        dt.Columns.Add("Style",            typeof(string));
        dt.Columns.Add("Remarks",          typeof(string));
        dt.Columns.Add("StoreId",          typeof(string));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                (object?)r.ContNo           ?? DBNull.Value,
                (object?)r.TrnDate          ?? DBNull.Value,
                (object?)r.Time1            ?? DBNull.Value,
                (object?)r.UPC              ?? DBNull.Value,
                (object?)r.Itemcode         ?? DBNull.Value,
                (object?)r.GroupCode        ?? DBNull.Value,
                (object?)r.Season           ?? DBNull.Value,
                (object?)r.Department       ?? DBNull.Value,
                (object?)r.Division         ?? DBNull.Value,
                (object?)r.FinalResult      ?? DBNull.Value,
                (object?)r.ResultType       ?? DBNull.Value,
                (object?)r.Qty              ?? DBNull.Value,
                (object?)r.QtyIssue         ?? DBNull.Value,
                (object?)r.Itemname         ?? DBNull.Value,
                (object?)r.Barcode          ?? DBNull.Value,
                r.SalesPrice.HasValue ? (object)r.SalesPrice.Value.ToString("F4") : DBNull.Value,
                (object?)r.TcmContno        ?? DBNull.Value,
                (object?)r.BuildingCategory ?? DBNull.Value,
                (object?)r.LPMDt            ?? DBNull.Value,
                (object?)r.LPMBoxNO         ?? DBNull.Value,
                (object?)r.ORAPONo          ?? DBNull.Value,
                (object?)r.Style            ?? DBNull.Value,
                (object?)r.Remarks          ?? DBNull.Value,
                (object?)r.StoreID          ?? DBNull.Value);
        }
        return dt;
    }

    private static string DestinationLabel(DataSyncDestination d) => d switch
    {
        DataSyncDestination.AzureWmsDb      => "Azure WMS DB",
        DataSyncDestination.WmsProductionDb => "WMS-Prod-DB",
        _                                   => d.ToString(),
    };

    // Mirror the columns we read out of LPMSIM. Order matches the SQL in SyncAsync.
    private sealed record SourceRow(
        int?      BatchNo,
        string?   ContNo,
        string?   Country,
        DateTime? TrnDate,
        TimeSpan? Time1,
        string?   UPC,
        string?   Itemcode,
        string?   Barcode,
        string?   GroupCode,
        int?      Qty,
        int?      SkuMax,
        int?      AllocatedQty,
        int?      PrevAllocatedQty,
        int?      QtyIssue,
        string?   StoreID,
        string?   TcmContno,
        string?   Itemname,
        string?   BuildingCategory,
        DateTime? LPMDt,
        string?   LPMBoxNO,
        string?   ORAPONo,
        string?   Division,
        string?   Brand,
        int?      DivCode,
        string?   Department,
        string?   Season,
        string?   Style,
        string?   Size,
        decimal?  SalesPrice,
        string?   ResultType,
        string?   FinalResult,
        string?   Result,
        string?   Remarks,
        double?   OTS,
        string?   Color,
        string?   Gender,
        string?   HsCode,
        string?   Class,
        string?   Family,
        string?   Subclass);
}

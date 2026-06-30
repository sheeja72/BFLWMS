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
                 WHERE ContNo IN @cs
                   AND Destination IN ('AzureWmsDb','WmsProductionDb')",
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

    /// <summary>True if this ContNo has any prior allocation-destination sync log entry
    /// (Q4 gate). KNB-box log rows are NOT counted — KNB sync has its own gate.</summary>
    public async Task<bool> IsAlreadySyncedAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return false;
        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT TOP 1 1
                FROM dbo.WMS_ContAllocationDataSync_Log WITH (NOLOCK)
               WHERE ContNo = @c
                 AND Destination IN ('AzureWmsDb','WmsProductionDb')",
            new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return hit == 1;
    }

    /// <summary>True if dbo.WmsKNBBoxes already has rows for this Country + ContNo
    /// — used to skip the KNB pull on subsequent syncs.</summary>
    public async Task<bool> IsKnbBoxesPulledAsync(string contno, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno)) return false;
        await using var c = OpenWms();
        var hit = await c.ExecuteScalarAsync<int?>(new CommandDefinition(
            @"SELECT TOP 1 1 FROM dbo.WmsKNBBoxes WITH (NOLOCK)
               WHERE Country = @country AND Contno = @c",
            new { country = user.Country, c = contno },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return hit == 1;
    }

    // ===================== Write-side =====================

    /// <summary>Sync entry point — runs the allocation copy AND the KNB-boxes
    /// pull for the same ContNo. The two have independent gates and produce
    /// their own log rows; the UI sees both in Recent Activity.</summary>
    public async Task<DataSyncResult> SyncAsync(string contno, DataSyncDestination destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contno))
            return new DataSyncResult(false, "Container number is required.", null, 0);
        contno = contno.Trim();

        var alloc = await TryCopyAllocationAsync(contno, destination, ct);
        var knb   = await TryCopyKnbBoxesAsync(contno, ct);

        // Both produce log rows. Combine for the page banner.
        var ok      = alloc.Ok || knb.Ok;
        var parts   = new[] { alloc.Message, knb.Message }.Where(m => !string.IsNullOrEmpty(m));
        return new DataSyncResult(
            Ok: ok,
            Message: string.Join(" | ", parts),
            SyncId: alloc.SyncId ?? knb.SyncId,
            RowsCopied: alloc.RowsCopied + knb.RowsCopied);
    }

    // ----- pass 1: allocation copy (with Q4 gate) -----
    private async Task<DataSyncResult> TryCopyAllocationAsync(string contno, DataSyncDestination destination, CancellationToken ct)
    {
        if (await IsAlreadySyncedAsync(contno, ct))
            return new DataSyncResult(false,
                $"Allocation: container {contno} was already synced before — skipped.",
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
            return new DataSyncResult(false, $"Allocation: no approved rows found for container {contno}.", null, 0);

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
        catch (Exception ex) { error = ex.Message; }

        var syncId = await WriteLogRowAsync(
            contno, primaryBatchNo, destination, totalAllocatedQty,
            status: error is null ? "Success" : "Failed", error, ct);

        return error is null
            ? new DataSyncResult(true,
                $"Allocation: {rowsCopied:N0} rows copied to {DestinationLabel(destination)}.",
                syncId, rowsCopied)
            : new DataSyncResult(false,
                $"Allocation to {DestinationLabel(destination)} failed: {error}",
                syncId, rowsCopied);
    }

    // ----- pass 2: KNB boxes copy (independent gate) -----
    private async Task<DataSyncResult> TryCopyKnbBoxesAsync(string contno, CancellationToken ct)
    {
        if (await IsKnbBoxesPulledAsync(contno, ct))
        {
            var skipId = await WriteLogRowAsync(
                contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Skipped",
                error: "dbo.WmsKNBBoxes already has rows for this Country + ContNo.", ct);
            return new DataSyncResult(true,
                $"KNB boxes: skipped — Azure mirror already has rows for {contno}.",
                skipId, 0);
        }

        List<KnbBoxRow> rows;
        try
        {
            await using var src = OpenOnPremBackup();
            rows = (await src.QueryAsync<KnbBoxRow>(new CommandDefinition(
                @"SELECT palletno, Boxno, Contno, trndate, userid, closed, Remarks, whouse
                    FROM usa.dbo.KNBBoxes WITH (NOLOCK)
                   WHERE Contno = @c",
                new { c = contno }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct))).ToList();
        }
        catch (Exception ex)
        {
            var failId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Failed", error: $"Source read: {ex.Message}", ct);
            return new DataSyncResult(false, $"KNB boxes read failed: {ex.Message}", failId, 0);
        }

        if (rows.Count == 0)
        {
            var emptyId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes, 0,
                status: "Empty", error: $"usa.dbo.KNBBoxes returned no rows for Contno = {contno}.", ct);
            return new DataSyncResult(true, $"KNB boxes: source has no rows for {contno}.", emptyId, 0);
        }

        string? writeError = null;
        try
        {
            var dt = BuildKnbBoxDataTable(user.Country ?? "", rows);
            await using var conn = OpenWms();
            using var bulk = new SqlBulkCopy(conn)
            {
                DestinationTableName = "dbo.WmsKNBBoxes",
                BatchSize            = 1000,
                BulkCopyTimeout      = CommandTimeoutSeconds,
            };
            foreach (System.Data.DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }
        catch (Exception ex) { writeError = ex.Message; }

        var logId = await WriteLogRowAsync(contno, null, DataSyncDestination.WmsKnbBoxes,
            totalAllocatedQty: rows.Count,
            status: writeError is null ? "Success" : "Failed",
            error: writeError, ct);

        return writeError is null
            ? new DataSyncResult(true,  $"KNB boxes: {rows.Count:N0} row(s) pulled.", logId, rows.Count)
            : new DataSyncResult(false, $"KNB boxes write failed: {writeError}",       logId, 0);
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
                ParseDecimalOrDbNull(r.SalesPrice),
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
                (object?)r.SalesPrice       ?? DBNull.Value,
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

    private static object ParseDecimalOrDbNull(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : (object)DBNull.Value;

    private static string DestinationLabel(DataSyncDestination d) => d switch
    {
        DataSyncDestination.AzureWmsDb      => "Azure WMS DB",
        DataSyncDestination.WmsProductionDb => "WMS-Prod-DB",
        DataSyncDestination.WmsKnbBoxes     => "Azure WMS — KNB Boxes",
        _                                   => d.ToString(),
    };

    private static System.Data.DataTable BuildKnbBoxDataTable(string country, List<KnbBoxRow> rows)
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("Country",  typeof(string));
        dt.Columns.Add("palletno", typeof(string));
        dt.Columns.Add("Boxno",    typeof(string));
        dt.Columns.Add("Contno",   typeof(string));
        dt.Columns.Add("trndate",  typeof(DateTime));
        dt.Columns.Add("userid",   typeof(string));
        dt.Columns.Add("closed",   typeof(string));
        dt.Columns.Add("Remarks",  typeof(string));
        dt.Columns.Add("whouse",   typeof(string));

        foreach (var r in rows)
        {
            dt.Rows.Add(
                country ?? "",
                (object?)r.palletno ?? DBNull.Value,
                r.Boxno  ?? "",                       // PK component — must be non-null
                r.Contno ?? "",                       // PK component — must be non-null
                (object?)r.trndate  ?? DBNull.Value,
                (object?)r.userid   ?? DBNull.Value,
                (object?)r.closed   ?? DBNull.Value,
                (object?)r.Remarks  ?? DBNull.Value,
                (object?)r.whouse   ?? DBNull.Value);
        }
        return dt;
    }

    // Materialised from usa.dbo.KNBBoxes — class (not record) so Dapper does
    // per-column type coercion, matching the SourceRow pattern.
    private sealed class KnbBoxRow
    {
        public string?   palletno { get; set; }
        public string?   Boxno    { get; set; }
        public string?   Contno   { get; set; }
        public DateTime? trndate  { get; set; }
        public string?   userid   { get; set; }
        public string?   closed   { get; set; }
        public string?   Remarks  { get; set; }
        public string?   whouse   { get; set; }
    }

    // Mirror the columns we read out of LPMSIM. A class with settable
    // properties (not a positional record) — Dapper's record-constructor
    // matching is strict on parameter type, but property-based hydration
    // does per-column type coercion (string -> decimal etc.), which we
    // need because LPMSIM's SalesPrice is varchar while the Azure mirror
    // column is decimal.
    private sealed class SourceRow
    {
        public int?      BatchNo          { get; set; }
        public string?   ContNo           { get; set; }
        public string?   Country          { get; set; }
        public DateTime? TrnDate          { get; set; }
        public TimeSpan? Time1            { get; set; }
        public string?   UPC              { get; set; }
        public string?   Itemcode         { get; set; }
        public string?   Barcode          { get; set; }
        public string?   GroupCode        { get; set; }
        public int?      Qty              { get; set; }
        public int?      SkuMax           { get; set; }
        public int?      AllocatedQty     { get; set; }
        public int?      PrevAllocatedQty { get; set; }
        public int?      QtyIssue         { get; set; }
        public string?   StoreID          { get; set; }
        public string?   TcmContno        { get; set; }
        public string?   Itemname         { get; set; }
        public string?   BuildingCategory { get; set; }
        public DateTime? LPMDt            { get; set; }
        public string?   LPMBoxNO         { get; set; }
        public string?   ORAPONo          { get; set; }
        public string?   Division         { get; set; }
        public string?   Brand            { get; set; }
        public int?      DivCode          { get; set; }
        public string?   Department       { get; set; }
        public string?   Season           { get; set; }
        public string?   Style            { get; set; }
        public string?   Size             { get; set; }
        public string?   SalesPrice       { get; set; }  // varchar on LPMSIM; parsed to decimal for the Azure mirror
        public string?   ResultType       { get; set; }
        public string?   FinalResult      { get; set; }
        public string?   Result           { get; set; }
        public string?   Remarks          { get; set; }
        public double?   OTS              { get; set; }
        public string?   Color            { get; set; }
        public string?   Gender           { get; set; }
        public string?   HsCode           { get; set; }
        public string?   Class            { get; set; }
        public string?   Family           { get; set; }
        public string?   Subclass         { get; set; }
    }
}

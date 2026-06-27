using Wms.Core;
using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Owns the snapshot tables that back the Missing / Excess report.
///
/// Two kinds of refresh:
///   - RefreshDayAsync — wipe + repopulate snapshot rows for ONE (Country, Date).
///                       Used by the nightly job (T-1) and by on-demand "Refresh Now".
///   - BackfillRangeAsync — same as RefreshDayAsync per day, looping over a range.
///                       Used by the Admin page "Backfill from 2026-02-21" button.
///
/// Source reads use the OnPremBackup connection (3-part naming to LPMSIM /
/// bfldata / usa / racks / hodata / datareporting). Snapshot writes go to the
/// Azure WMS DB. Job-log writes go there too.
/// </summary>
public class MissingExcessSnapshotService(IOnPremConnectionResolver resolver, ICurrentUser user)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 600;   // backfill batches can take a while
    public const string JobName = "MissingExcessSnapshot";
    public static readonly DateTime BackfillFloor = new(2026, 2, 21);

    private static string WithConnectTimeout(string cs)
    {
        var b = new SqlConnectionStringBuilder(cs) { ConnectTimeout = ConnectTimeoutSeconds };
        return b.ConnectionString;
    }

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    private SqlConnection OpenOnPremBackup()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetOnPremBackupConnectionString()));
        c.Open();
        return c;
    }

    // ====================== Country config ======================

    public async Task<List<RptCountryConfigRow>> GetCountryConfigAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<RptCountryConfigRow>(new CommandDefinition(
            "SELECT Country, IsActive, UpdatedTS, UpdatedBy FROM dbo.WmsRptCountryConfig ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<string>> GetActiveCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT Country FROM dbo.WmsRptCountryConfig WHERE IsActive = 1 ORDER BY Country",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetCountryActiveAsync(string country, bool isActive, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            MERGE dbo.WmsRptCountryConfig AS t
            USING (SELECT @c AS Country) AS s
              ON t.Country = s.Country
            WHEN MATCHED THEN
              UPDATE SET IsActive = @a, UpdatedTS = SYSDATETIME(), UpdatedBy = @u
            WHEN NOT MATCHED THEN
              INSERT (Country, IsActive, UpdatedTS, UpdatedBy)
              VALUES (@c, @a, SYSDATETIME(), @u);",
            new { c = country, a = isActive, u = user.Name },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // ====================== Job-run log ======================

    public async Task<long> StartJobRunAsync(string mode, string? country, string triggeredBy, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var id = await c.ExecuteScalarAsync<long>(new CommandDefinition(@"
            INSERT INTO dbo.WmsRptJobRun (JobName, Country, Mode, StartTS, Status, TriggeredBy)
            OUTPUT INSERTED.RunId
            VALUES (@j, @c, @m, SYSDATETIME(), 'Running', @t);",
            new { j = JobName, c = country, m = mode, t = triggeredBy },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return id;
    }

    public async Task FinishJobRunAsync(long runId, string status, int? rowsProcessed, int? datesProcessed, string? errorMessage, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.WmsRptJobRun
               SET EndTS = SYSDATETIME(), Status = @s, RowsProcessed = @r,
                   DatesProcessed = @d, ErrorMessage = @e
             WHERE RunId = @id;",
            new { id = runId, s = status, r = rowsProcessed, d = datesProcessed, e = errorMessage },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    public async Task<List<RptJobRunRow>> GetRecentRunsAsync(int top = 50, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<RptJobRunRow>(new CommandDefinition($@"
            SELECT TOP ({top}) RunId, JobName, Country, Mode, StartTS, EndTS,
                   Status, RowsProcessed, DatesProcessed, ErrorMessage, TriggeredBy
              FROM dbo.WmsRptJobRun
             ORDER BY StartTS DESC",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    // ====================== Refresh one day for one country ======================
    // Wipes the (Country, ClosedDt) slice in all 3 snapshot tables, then re-pulls
    // from the source via the same #BadBoxes pattern ReportsService uses,
    // restricted to a single ClosedDt.
    public async Task<int> RefreshDayAsync(string country, DateTime day, CancellationToken ct = default)
    {
        var d = day.Date;
        await using var src = OpenOnPremBackup();
        await using var dst = OpenWms();

        // 1) Pull all three result sets in one batch (one Dapper call) so the
        //    temp table is alive across them. QueryMultipleAsync returns each
        //    result set in order.
        const string sql = @"
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#BadBoxes') IS NOT NULL DROP TABLE #BadBoxes;
            SELECT DISTINCT
                cr.Palletno  AS BoxNo,
                cr.Trndate   AS ClosedDt,
                cr.closedby  AS ClosedBy,
                ISNULL(cr.missqty,0) AS MissQty,
                ISNULL(cr.zeroqty,0) AS ExcessQty
              INTO #BadBoxes
              FROM bfldata.dbo.CloseR1pallet cr WITH (NOLOCK)
             WHERE cr.Trndate = @day
               AND ISNULL(cr.missqty,0) + ISNULL(cr.zeroqty,0) > 0
               AND EXISTS (
                   SELECT 1 FROM usa.dbo.AMEChecking a WITH (NOLOCK)
                    WHERE a.contno = cr.Palletno AND a.Trndate >= @floor);
            CREATE CLUSTERED INDEX IX_BadBoxes ON #BadBoxes (BoxNo);

            -- Result 1: Box Summary (one per BoxNo/ClosedDt/ClosedBy)
            SELECT BoxNo, ClosedDt, ClosedBy,
                   SUM(MissQty) AS MissQty, SUM(ExcessQty) AS ExcessQty
              FROM #BadBoxes
             GROUP BY BoxNo, ClosedDt, ClosedBy;

            -- Result 2: Box Detail — one row per (box, item) with Missing + Excess as columns.
            -- Missing fires only when Status='' and QtyIssued < qty. Excess = QtyIssued
            -- when Status<>'' (the issued qty IS the excess per the user spec).
            SELECT b.ClosedDt,
                   d.BoxNo, d.preparedby AS PreparedBy, d.itemcode AS ItemCode,
                   d.qty AS Qty, d.QtyIssued AS QtyIssued,
                   CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                        THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                   CASE WHEN ISNULL(d.Status,'') <> ''
                        THEN d.QtyIssued          ELSE 0 END AS ExcessQty
              FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
              INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
             WHERE (ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty)
                OR (ISNULL(d.Status,'') <> '' AND d.QtyIssued > 0);

            -- Result 3: Item Summary per item, with current HOStock
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN d.QtyIssued          ELSE 0 END AS ExcessQty
                  FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                  INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
            ), agg AS (
                SELECT itemcode,
                       SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty
                  FROM base GROUP BY itemcode
                 HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            ), soh AS (
                SELECT itemcode, SUM(soh) AS HOStock
                  FROM racks.dbo.lpm_locstock WITH (NOLOCK)
                 WHERE itemcode IN (SELECT itemcode FROM agg)
                 GROUP BY itemcode
            )
            SELECT a.itemcode             AS ItemCode,
                   im.description         AS ItemName,
                   sub.Division           AS Division,
                   sub.Department         AS Department,
                   a.MissingQty           AS MissingQty,
                   a.ExcessQty            AS ExcessQty,
                   ISNULL(s.HOStock, 0)   AS HOStock
              FROM agg a
              LEFT JOIN hodata.dbo.itemmaster           im  WITH (NOLOCK) ON im.itemcode  = a.itemcode
              LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
              LEFT JOIN soh s                                              ON s.itemcode  = a.itemcode;";

        BoxSummaryRow[] boxSummary;
        BoxDetailCombinedDayRow[] boxDetail;
        ItemSummaryReportRow[] itemSummary;
        await using (var grid = await src.QueryMultipleAsync(new CommandDefinition(sql,
            new { day = d, floor = BackfillFloor }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct)))
        {
            boxSummary  = (await grid.ReadAsync<BoxSummaryRow>()).ToArray();
            boxDetail   = (await grid.ReadAsync<BoxDetailCombinedDayRow>()).ToArray();
            itemSummary = (await grid.ReadAsync<ItemSummaryReportRow>()).ToArray();
        }

        // 2) Wipe + reload the (Country, day) slice in all three snapshot tables.
        await using var tx = (SqlTransaction)await dst.BeginTransactionAsync(ct);
        try
        {
            await dst.ExecuteAsync(new CommandDefinition(@"
                DELETE FROM dbo.WmsRptMissingExcess_BoxSummary  WHERE Country = @c AND ClosedDt = @d;
                DELETE FROM dbo.WmsRptMissingExcess_BoxDetail   WHERE Country = @c AND ClosedDt = @d;
                DELETE FROM dbo.WmsRptMissingExcess_ItemSummary WHERE Country = @c AND ClosedDt = @d;",
                new { c = country, d = d }, transaction: tx,
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

            foreach (var r in boxSummary)
            {
                await dst.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO dbo.WmsRptMissingExcess_BoxSummary (Country, BoxNo, ClosedDt, ClosedBy, MissQty, ExcessQty)
                    VALUES (@c, @b, @d, @cb, @m, @x);",
                    new { c = country, b = r.BoxNo, d = r.ClosedDt ?? d, cb = r.ClosedBy, m = r.MissQty, x = r.ExcessQty },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }
            foreach (var r in boxDetail)
            {
                await dst.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO dbo.WmsRptMissingExcess_BoxDetail (Country, ClosedDt, BoxNo, PreparedBy, ItemCode, Qty, QtyIssued, MissingQty, ExcessQty)
                    VALUES (@c, @d, @b, @p, @i, @q, @qi, @m, @x);",
                    new { c = country, d = r.ClosedDt ?? d, b = r.BoxNo, p = r.PreparedBy, i = r.ItemCode, q = r.Qty, qi = r.QtyIssued, m = r.MissingQty, x = r.ExcessQty },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }
            foreach (var r in itemSummary)
            {
                await dst.ExecuteAsync(new CommandDefinition(@"
                    INSERT INTO dbo.WmsRptMissingExcess_ItemSummary (Country, ClosedDt, ItemCode, ItemName, Division, Department, MissingQty, ExcessQty, HOStock)
                    VALUES (@c, @d, @i, @n, @v, @p, @m, @x, @h);",
                    new { c = country, d = d, i = r.ItemCode, n = r.ItemName, v = r.Division, p = r.Department, m = r.MissingQty, x = r.ExcessQty, h = r.HOStock },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            }

            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }

        return boxSummary.Length + boxDetail.Length + itemSummary.Length;
    }

    /// <summary>Backfill a date range (inclusive), one day at a time.</summary>
    public async Task<(int days, int rows)> BackfillRangeAsync(string country, DateTime fromDt, DateTime toDt, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (fromDt < BackfillFloor) fromDt = BackfillFloor;
        var days = 0; var rows = 0;
        for (var d = fromDt.Date; d <= toDt.Date; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{country} {d:yyyy-MM-dd}");
            rows += await RefreshDayAsync(country, d, ct);
            days++;
        }
        return (days, rows);
    }

    /// <summary>Day in the BoxDetailCombined result set carries a ClosedDt the
    /// service maps back into the snapshot row.</summary>
    internal record BoxDetailCombinedDayRow(DateTime? ClosedDt, string? BoxNo, string? PreparedBy, string? ItemCode, int Qty, int QtyIssued, int MissingQty, int ExcessQty);
}

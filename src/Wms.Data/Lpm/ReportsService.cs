using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Reports service — Missing/Excess from Production and related cross-DB reports.
///
/// Country list comes from the UAE master backup connection
/// (bfldata.dbo.datasettings.Simcountry). Per-report queries use the
/// per-country connection (IOnPremConnectionResolver.GetCountryConnectionString).
/// </summary>
public class ReportsService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 60;

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

    // All report queries currently hit OnPremBackup (UAE master) via 3-part naming —
    // same as ContainerAllocationService — since per-country connection strings
    // aren't configured. Wire to GetCountryConnectionString later if needed.
    private SqlConnection OpenCountry(string country) => OpenOnPremBackup();

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Simcountry FROM bfldata.dbo.datasettings WHERE Simcountry IS NOT NULL ORDER BY Simcountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    // SQL prefix that materialises #BadBoxes — must be prepended to whatever
    // query references the temp table, so the build + read happen in the
    // SAME Dapper command (= same SQL Server session). Splitting it across
    // two ExecuteAsync calls dropped the temp table between commands in
    // testing ("Invalid object name '#BadBoxes'").
    private const string BadBoxesPrefix = @"
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
         WHERE cr.Trndate >= @from AND cr.Trndate <= @to
           AND ISNULL(cr.missqty,0) + ISNULL(cr.zeroqty,0) > 0
           AND EXISTS (
               SELECT 1 FROM usa.dbo.AMEChecking a WITH (NOLOCK)
                WHERE a.contno = cr.Palletno AND a.Trndate >= @from);
        CREATE CLUSTERED INDEX IX_BadBoxes ON #BadBoxes (BoxNo);
        ";

    /// <summary>Box Summary — one row per (BoxNo, ClosedDt, ClosedBy).</summary>
    public async Task<List<BoxSummaryRow>> BoxSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxSummaryRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT BoxNo, ClosedDt, ClosedBy,
                   SUM(MissQty)   AS MissQty,
                   SUM(ExcessQty) AS ExcessQty
              FROM #BadBoxes
             GROUP BY BoxNo, ClosedDt, ClosedBy
             ORDER BY ClosedBy DESC, ClosedDt DESC",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Summary aggregated by month (yyyy-MM) — UI table.</summary>
    public async Task<List<BoxSummaryMonthRow>> BoxSummaryByMonthAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxSummaryMonthRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT CONVERT(varchar(7), ClosedDt, 120)  AS [Month],
                   COUNT(DISTINCT BoxNo)               AS BoxCount,
                   SUM(MissQty)                        AS MissQty,
                   SUM(ExcessQty)                      AS ExcessQty
              FROM #BadBoxes
             GROUP BY CONVERT(varchar(7), ClosedDt, 120)
             ORDER BY [Month]",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Detail combined — Missing + Excess rows tagged with a Type column.</summary>
    public async Task<List<BoxDetailCombinedRow>> BoxDetailCombinedAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxDetailCombinedRow>(new CommandDefinition(BadBoxesPrefix + @"
            SELECT 'Missing' AS [Type],
                   d.BoxNo, d.preparedby AS PreparedBy, d.itemcode AS ItemCode,
                   d.qty AS Qty, d.QtyIssued AS QtyIssued, (d.qty - d.QtyIssued) AS Diff
              FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
              INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
             WHERE d.QtyIssued < d.qty AND ISNULL(d.Status,'') = ''
            UNION ALL
            SELECT 'Excess' AS [Type],
                   d.BoxNo, d.preparedby AS PreparedBy, d.itemcode AS ItemCode,
                   d.qty AS Qty, d.QtyIssued AS QtyIssued, (d.QtyIssued - d.qty) AS Diff
              FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
              INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
             WHERE ISNULL(d.Status,'') <> ''
             ORDER BY BoxNo, ItemCode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Item Summary aggregated by (Division, Department) — UI table.</summary>
    public async Task<List<ItemSummaryByDivDeptRow>> ItemSummaryByDivDeptAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<ItemSummaryByDivDeptRow>(new CommandDefinition(BadBoxesPrefix + @"
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN (d.QtyIssued - d.qty) ELSE 0 END AS ExcessQty
                  FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                  INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
            ), agg AS (
                SELECT itemcode, SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty
                  FROM base
                 GROUP BY itemcode
                HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            ), soh AS (
                SELECT itemcode, SUM(soh) AS HOStock
                  FROM racks.dbo.lpm_locstock WITH (NOLOCK)
                 WHERE itemcode IN (SELECT itemcode FROM agg)
                 GROUP BY itemcode
            )
            SELECT sub.Division                AS Division,
                   sub.Department              AS Department,
                   SUM(a.MissingQty)           AS MissingQty,
                   SUM(a.ExcessQty)            AS ExcessQty,
                   SUM(ISNULL(s.HOStock, 0))   AS HOStock
              FROM agg a
              LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
              LEFT JOIN soh s                                              ON s.itemcode  = a.itemcode
             GROUP BY sub.Division, sub.Department
             ORDER BY sub.Division, sub.Department",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Item Summary — per ItemCode totals of missing + excess, joined with item name
    /// (hodata.itemmaster), hierarchy/division/department (datareporting.vupc_subclass),
    /// and HO stock (racks.lpm_locstock).
    /// </summary>
    // ===================== Non-LPM WH Stock report (ported from LPMSIM) =====================
    /// <summary>
    /// Ported from LPMSIM (WhHoStockService.GetNonLpmWhStockAsync). For every
    /// configured country (UAE, KSA, Kuwait, Qatar, Bahrain, MALAYSIA), sums
    /// Non-LPM eligible WH stock per Division × Season into one row per
    /// (Country, Division). Filter: LPMDt IS NULL, ShopEligible != 'E',
    /// PalletCategory = 'ELIGIBLE'. Season from whboxitems.Season.
    /// A misconfigured / unreadable country is skipped (not fatal).
    /// </summary>
    public async Task<List<NonLpmWhStockRow>> GetNonLpmWhStockAsync(CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();

        // 1) item → division map ONCE (global master tables)
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                IF OBJECT_ID('tempdb..#NlItemDiv') IS NOT NULL DROP TABLE #NlItemDiv;
                SELECT u.itemcode, Division = MIN(sm.Division)
                  INTO #NlItemDiv
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                 WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
                 GROUP BY u.itemcode;
                CREATE CLUSTERED INDEX IX_NlItemDiv ON #NlItemDiv (itemcode);";
            ddl.CommandTimeout = 120;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // 2) Fixed country set (per spec — excludes ECOM / virtual pseudo-countries).
        var countries = new List<string> { "UAE", "KSA", "Kuwait", "Qatar", "Bahrain", "MALAYSIA" };

        var rows = new List<NonLpmWhStockRow>();
        foreach (var country in countries)
        {
            string whSrc;
            try { whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct); }
            catch { continue; }   // no DataName / unreadable → skip

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT Division = ISNULL(id.Division, '(no division)'),
                           Summer = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) <> 'W'
                                             THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END),
                           Winter = SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) =  'W'
                                             THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END)
                      FROM {whSrc} w
                      LEFT JOIN #NlItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.LPMDt IS NULL
                       AND ISNULL(w.ShopEligible,'') <> 'E'
                       AND UPPER(ISNULL(w.PalletCategory,'')) = 'ELIGIBLE'
                     GROUP BY ISNULL(id.Division, '(no division)')
                    HAVING SUM(CAST(ISNULL(w.Qty,0) AS bigint)) <> 0
                     ORDER BY Division;";
                cmd.CommandTimeout = 300;
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    rows.Add(new NonLpmWhStockRow(
                        Country:  country,
                        Division: rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        Summer:   rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1),
                        Winter:   rdr.IsDBNull(2) ? 0L : rdr.GetInt64(2)));
                }
            }
            catch { /* one country's WH table missing/unreadable — skip it */ }
        }
        return rows;
    }

    public async Task<List<ItemSummaryReportRow>> ItemSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<ItemSummaryReportRow>(new CommandDefinition(BadBoxesPrefix + @"
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN (d.QtyIssued - d.qty) ELSE 0 END AS ExcessQty
                  FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                  INNER JOIN #BadBoxes b ON b.BoxNo = d.BoxNo
            ), agg AS (
                SELECT itemcode,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty
                  FROM base
                 GROUP BY itemcode
                HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            ), soh AS (
                SELECT itemcode, SUM(soh) AS HOStock
                  FROM racks.dbo.lpm_locstock WITH (NOLOCK)
                 WHERE itemcode IN (SELECT itemcode FROM agg)
                 GROUP BY itemcode
            )
            SELECT
                a.itemcode                AS ItemCode,
                im.description            AS ItemName,
                sub.Division              AS Division,
                sub.Department            AS Department,
                a.MissingQty              AS MissingQty,
                a.ExcessQty               AS ExcessQty,
                ISNULL(s.HOStock, 0)      AS HOStock
              FROM agg a
              LEFT JOIN hodata.dbo.itemmaster           im  WITH (NOLOCK) ON im.itemcode  = a.itemcode
              LEFT JOIN datareporting.dbo.vupc_subclass sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
              LEFT JOIN soh s                                              ON s.itemcode  = a.itemcode
             ORDER BY a.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}

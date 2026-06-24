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

    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetWmsAzureConnectionString()));
        c.Open();
        return c;
    }

    // All report queries currently hit OnPremBackup (UAE master) via 3-part naming —
    // same as ContainerAllocationService — since per-country connection strings
    // aren't configured. Wire to GetCountryConnectionString later if needed.
    private SqlConnection OpenCountry(string country) => OpenOnPremBackup();

    // ===================== Snapshot-backed reads (Azure WMS DB) =====================
    // These are the methods the Missing/Excess page calls on Load. They read
    // pre-computed snapshot tables populated by MissingExcessSnapshotService.

    public async Task<List<BoxSummaryMonthRow>> BoxSummaryByMonthFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxSummaryMonthRow>(new CommandDefinition(@"
            SELECT CONVERT(varchar(7), ClosedDt, 120)  AS [Month],
                   COUNT(DISTINCT BoxNo)                AS BoxCount,
                   SUM(MissQty)                         AS MissQty,
                   SUM(ExcessQty)                       AS ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             GROUP BY CONVERT(varchar(7), ClosedDt, 120)
             ORDER BY [Month]",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<BoxSummaryRow>> BoxSummaryFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxSummaryRow>(new CommandDefinition(@"
            SELECT BoxNo, ClosedDt, ClosedBy, MissQty, ExcessQty
              FROM dbo.WmsRptMissingExcess_BoxSummary
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY ClosedBy DESC, ClosedDt DESC",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<BoxDetailCombinedRow>> BoxDetailCombinedFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<BoxDetailCombinedRow>(new CommandDefinition(@"
            SELECT [Type], BoxNo, PreparedBy, ItemCode, Qty, QtyIssued, Diff
              FROM dbo.WmsRptMissingExcess_BoxDetail
             WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
             ORDER BY BoxNo, ItemCode",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ItemSummaryByDivDeptRow>> ItemSummaryByDivDeptFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        // HOStock is point-in-time at snapshot run. For the (Division, Department)
        // aggregate we take MAX HOStock per item then SUM across the items in
        // the group so an item's stock isn't double-counted across multiple days.
        var rows = await c.QueryAsync<ItemSummaryByDivDeptRow>(new CommandDefinition(@"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode, MAX(Division) AS Division, MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty, SUM(ExcessQty) AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT Division, Department,
                   SUM(MissingQty) AS MissingQty,
                   SUM(ExcessQty)  AS ExcessQty,
                   SUM(HOStock)    AS HOStock
              FROM itemAgg
             GROUP BY Division, Department
             ORDER BY Division, Department",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<ItemSummaryReportRow>> ItemSummaryFromSnapshotAsync(
        string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenWms();
        var rows = await c.QueryAsync<ItemSummaryReportRow>(new CommandDefinition(@"
            ;WITH itemAgg AS (
                SELECT Country, ItemCode,
                       MAX(ItemName)   AS ItemName,
                       MAX(Division)   AS Division,
                       MAX(Department) AS Department,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty,
                       MAX(HOStock)    AS HOStock
                  FROM dbo.WmsRptMissingExcess_ItemSummary
                 WHERE Country = @c AND ClosedDt BETWEEN @from AND @to
                 GROUP BY Country, ItemCode
            )
            SELECT ItemCode, ItemName, Division, Department,
                   MissingQty, ExcessQty, HOStock
              FROM itemAgg
             ORDER BY ItemCode",
            new { c = country, from = fromDt.Date, to = toDt.Date },
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

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
    // ===================== Production Summary report (ported from LPMSIM) =====================
    /// <summary>
    /// Ported from LPMSIM (ProductionCheckingReportService.GetAsync, UAE path only).
    /// Reads usa.dbo.amechecking scans against LPMSIM.dbo.LPMSIM_Batch country filter
    /// + Sources-derived Kind, joins Datareporting for Division, and returns three
    /// result sets: detailed rows / summary rows / overall store qty. Transfer Qty
    /// is fetched separately from bfldata.dbo.DailyCountCategoryTrf (UAE only).
    /// </summary>
    public async Task<ProductionCheckingResult> GetProductionCheckingAsync(
        string country, DateTime fromDate, DateTime toDateInclusive, CancellationToken ct = default)
    {
        if (!string.Equals(country, "UAE", StringComparison.OrdinalIgnoreCase))
            return new ProductionCheckingResult(new(), new(), 0, 0);  // non-UAE not configured yet

        // Production day = WH-shift window [D 06:00 GST, D+1 06:00 GST). Scans
        // before 06:00 on calendar date D count toward D-1's shift.
        var fromInclusive       = fromDate.Date.AddHours(6);
        var toExclusive         = toDateInclusive.Date.AddDays(1).AddHours(6);
        var fromDateOnly        = fromDate.Date;
        var toDateExclusiveOnly = toDateInclusive.Date.AddDays(2);

        var rows    = new List<ProductionCheckingRow>();
        var summary = new List<ProductionCheckingSummaryRow>();
        int overallStoreQty = 0;

        await using var conn = OpenOnPremBackup();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 300;
            cmd.CommandText = @"
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Scans')     IS NOT NULL DROP TABLE #Scans;
IF OBJECT_ID('tempdb..#BatchKind') IS NOT NULL DROP TABLE #BatchKind;
IF OBJECT_ID('tempdb..#ItemDiv')   IS NOT NULL DROP TABLE #ItemDiv;

-- 1) Materialize the amechecking slice ONCE.
SELECT
    BatchNo = CASE
                  WHEN CHARINDEX('BP(', c.CmpName) > 0
                  THEN TRY_CAST(SUBSTRING(c.CmpName,
                                          CHARINDEX('BP(', c.CmpName) + 3,
                                          CHARINDEX(')',  c.CmpName, CHARINDEX('BP(', c.CmpName))
                                          - CHARINDEX('BP(', c.CmpName) - 3) AS bigint)
                  ELSE NULL
              END,
    Itemcode = ISNULL(c.Itemcode, ''),
    ShopName = ISNULL(c.ShopName, ''),
    Contno   = ISNULL(c.Contno,   ''),
    Result   = ISNULL(c.Result, -1),
    ProductionDay = CAST(CASE
                             WHEN TRY_CAST(c.Time1 AS time) >= '06:00:00'
                                 THEN c.TrnDate
                             ELSE DATEADD(day, -1, c.TrnDate)
                         END AS date)
  INTO #Scans
  FROM usa.dbo.amechecking c
 WHERE c.TrnDate >= @fromDateOnly
   AND c.TrnDate <  @toDateExclusiveOnly
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) >= @from
   AND CAST(c.TrnDate AS datetime) + CAST(c.Time1 AS datetime) <  @toExclusive;

CREATE CLUSTERED INDEX IX_Scans ON #Scans (BatchNo, Itemcode);

-- 2) Country gate. Delete wrong-country batches; keep orphans & nulls as Unknown.
DELETE s
  FROM #Scans s
  INNER JOIN LPMSIM.dbo.LPMSIM_Batch b ON b.LPMBatchNo = s.BatchNo
 WHERE b.Country <> @country;

-- 3) Per-BATCH Kind from LPMSIM_Batch.Sources.
SELECT
    b.LPMBatchNo,
    Kind = CASE
               WHEN b.Sources LIKE '%Non-LPM%'
                AND REPLACE(b.Sources, 'Non-LPM', '') LIKE '%LPM%' THEN 'Mixed'
               WHEN b.Sources LIKE '%Non-LPM%' THEN 'Non-LPM'
               WHEN b.Sources LIKE '%LPM%'     THEN 'LPM'
               ELSE 'Unknown'
           END
  INTO #BatchKind
  FROM LPMSIM.dbo.LPMSIM_Batch b
 WHERE b.LPMBatchNo IN (SELECT DISTINCT BatchNo FROM #Scans WHERE BatchNo IS NOT NULL);

CREATE CLUSTERED INDEX IX_BatchKind ON #BatchKind (LPMBatchNo);

-- 4) Division lookup.
SELECT u.itemcode,
       Division = MIN(sm.Division)
  INTO #ItemDiv
  FROM (SELECT DISTINCT Itemcode FROM #Scans WHERE Itemcode <> '') si
  INNER JOIN Datareporting.dbo.upc_subclass    u  ON u.itemcode = si.Itemcode
  INNER JOIN Datareporting.dbo.subclassmaster  sm ON sm.MH4ID   = u.MH4ID
 GROUP BY u.itemcode;

CREATE CLUSTERED INDEX IX_ItemDiv ON #ItemDiv (itemcode);

-- 5) Detailed result set.
SELECT
    s.ProductionDay,
    s.BatchNo,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END)
  FROM #Scans s
  LEFT JOIN #BatchKind bk ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv   idv ON idv.itemcode  = s.Itemcode
 GROUP BY s.ProductionDay, s.BatchNo, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          ISNULL(s.BatchNo, -1) DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 6) Summary result set.
SELECT
    s.ProductionDay,
    Kind     = ISNULL(bk.Kind, 'Unknown'),
    Division = ISNULL(NULLIF(idv.Division, ''), 'Unknown'),
    TotalScanned = COUNT_BIG(*),
    StoreQty     = SUM(CASE WHEN s.Result IN (0, 13) THEN 1 ELSE 0 END),
    UaeStoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'UAE'          THEN 1 ELSE 0 END),
    OmanStoreQty = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Oman'         THEN 1 ELSE 0 END),
    Ex2StoreQty  = SUM(CASE WHEN s.Result IN (0, 13) AND ds.SIMCountry = 'Ex2Locations' THEN 1 ELSE 0 END)
  FROM #Scans s
  LEFT JOIN #BatchKind         bk  ON bk.LPMBatchNo = s.BatchNo
  LEFT JOIN #ItemDiv           idv ON idv.itemcode  = s.Itemcode
  LEFT JOIN bfldata.dbo.DataSettings ds ON ds.ShopName = s.ShopName AND s.ShopName <> ''
 GROUP BY s.ProductionDay, ISNULL(bk.Kind, 'Unknown'), ISNULL(NULLIF(idv.Division, ''), 'Unknown')
 ORDER BY s.ProductionDay DESC,
          CASE ISNULL(bk.Kind, 'Unknown') WHEN 'LPM' THEN 0 WHEN 'Non-LPM' THEN 1 WHEN 'Mixed' THEN 2 ELSE 3 END,
          ISNULL(NULLIF(idv.Division, ''), 'Unknown');

-- 7) Overall Store Qty scalar.
SELECT OverallStoreQty = SUM(CASE WHEN Result IN (0, 13) THEN 1 ELSE 0 END) FROM #Scans;

DROP TABLE #Scans, #BatchKind, #ItemDiv;";
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@fromDateOnly",        fromDateOnly));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@toDateExclusiveOnly", toDateExclusiveOnly));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@from",                fromInclusive));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@toExclusive",         toExclusive));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@country",             country));

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new ProductionCheckingRow(
                    ProductionDay: rdr.GetDateTime(0),
                    BatchNo:       rdr.IsDBNull(1) ? null : rdr.GetInt64(1),
                    Kind:          rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                    Division:      rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3),
                    TotalScanned:  rdr.IsDBNull(4) ? 0 : rdr.GetInt64(4),
                    StoreQty:      rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5)));
            }
            if (await rdr.NextResultAsync(ct))
            {
                while (await rdr.ReadAsync(ct))
                {
                    summary.Add(new ProductionCheckingSummaryRow(
                        ProductionDay: rdr.GetDateTime(0),
                        Kind:          rdr.IsDBNull(1) ? "Unknown" : rdr.GetString(1),
                        Division:      rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                        TotalScanned:  rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3),
                        StoreQty:      rdr.IsDBNull(4) ? 0 : rdr.GetInt32(4),
                        UaeStoreQty:   rdr.IsDBNull(5) ? 0 : rdr.GetInt32(5),
                        OmanStoreQty:  rdr.IsDBNull(6) ? 0 : rdr.GetInt32(6),
                        Ex2StoreQty:   rdr.IsDBNull(7) ? 0 : rdr.GetInt32(7)));
                }
            }
            if (await rdr.NextResultAsync(ct) && await rdr.ReadAsync(ct))
            {
                overallStoreQty = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
            }
        }

        // Transfer Qty — separate query, UAE-only, bfldata source.
        long transferQty = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ISNULL(SUM(
                         ISNULL(HR0A,0)+ISNULL(HR1A,0)+ISNULL(HR2A,0)+ISNULL(HR3A,0)+ISNULL(HR4A,0)+
                         ISNULL(HR5A,0)+ISNULL(HR6A,0)+ISNULL(HR7A,0)+ISNULL(HR8A,0)+ISNULL(HR9A,0)+
                         ISNULL(HR10A,0)+ISNULL(HR11A,0)+ISNULL(HR12A,0)+ISNULL(HR13A,0)+ISNULL(HR14A,0)+
                         ISNULL(HR15A,0)+ISNULL(HR16A,0)+ISNULL(HR17A,0)+ISNULL(HR18A,0)+ISNULL(HR19A,0)+
                         ISNULL(HR20A,0)+ISNULL(HR21A,0)+ISNULL(HR22A,0)), 0) AS TransferQty
                  FROM bfldata.dbo.DailyCountCategoryTrf WITH (NOLOCK)
                 WHERE Warehouse = 'TECHNO'
                   AND TrnDate BETWEEN @from AND @to;";
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@from", fromDate.Date));
            cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@to",   toDateInclusive.Date));
            cmd.CommandTimeout = 60;
            var v = await cmd.ExecuteScalarAsync(ct);
            if (v is not null && v is not DBNull) transferQty = Convert.ToInt64(v);
        }

        return new ProductionCheckingResult(rows, summary, overallStoreQty, transferQty);
    }

    // ===================== LPM WH Stock report (ported from LPMSIM) =====================
    /// <summary>Distinct PalletCategory values from bfldata.dbo.pallettype.</summary>
    public async Task<List<string>> GetPalletCategoriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT PalletCategory
              FROM bfldata.dbo.pallettype
             WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
             ORDER BY PalletCategory",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Ported from LPMSIM (WhHoStockService.GetLpmWhStockAsync). For each SIM
    /// country (or just the caller-selected ones), sums purchased LPM warehouse
    /// stock (LPMDt IS NOT NULL AND ShopEligible &lt;&gt; 'E') by (Country,
    /// Division, Season, LPMDt Year/Month) for the chosen PalletCategory
    /// (default ELIGIBLE).
    /// </summary>
    public async Task<List<LpmWhStockCell>> GetLpmWhStockAsync(
        string palletCategory, IEnumerable<string>? onlyCountries = null, CancellationToken ct = default)
    {
        var pc = string.IsNullOrWhiteSpace(palletCategory) ? "ELIGIBLE" : palletCategory.Trim();
        await using var conn = OpenOnPremBackup();

        // 1) item → division map ONCE (global master tables)
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = @"
                IF OBJECT_ID('tempdb..#LpmItemDiv') IS NOT NULL DROP TABLE #LpmItemDiv;
                SELECT u.itemcode, Division = MIN(sm.Division)
                  INTO #LpmItemDiv
                  FROM Datareporting.dbo.upc_subclass    u
                  INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                 WHERE u.itemcode IS NOT NULL AND u.itemcode <> ''
                 GROUP BY u.itemcode;
                CREATE CLUSTERED INDEX IX_LpmItemDiv ON #LpmItemDiv (itemcode);";
            ddl.CommandTimeout = 120;
            await ddl.ExecuteNonQueryAsync(ct);
        }

        // 2) Which SIM countries to process?
        var countries = new List<string>();
        await using (var cc = conn.CreateCommand())
        {
            cc.CommandText = @"
                SELECT DISTINCT SIMCountry
                  FROM bfldata.dbo.DataSettings
                 WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
                 ORDER BY SIMCountry;";
            cc.CommandTimeout = 60;
            await using var rdr = await cc.ExecuteReaderAsync(ct);
            var only = onlyCountries?.Where(s => !string.IsNullOrWhiteSpace(s))
                                     .Select(s => s.Trim())
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (await rdr.ReadAsync(ct))
            {
                var ctry = rdr.GetString(0);
                if (only is null || only.Count == 0 || only.Contains(ctry.Trim()))
                    countries.Add(ctry);
            }
        }

        // 3) per-country aggregation to (Division, Season, Year, Month)
        var rows = new List<LpmWhStockCell>();
        foreach (var country in countries)
        {
            string whSrc;
            try { whSrc = await WhBoxItemsSource.ResolveAsync(conn, country, ct); }
            catch { continue; }

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT Division = ISNULL(id.Division, '(no division)'),
                           Season   = CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END,
                           Yr       = YEAR(w.LPMDt),
                           Mo       = MONTH(w.LPMDt),
                           Qty      = SUM(CAST(ISNULL(w.Qty,0) AS bigint))
                      FROM {whSrc} w
                      LEFT JOIN #LpmItemDiv id ON id.itemcode = w.ItemCode
                     WHERE w.LPMDt IS NOT NULL
                       AND ISNULL(w.ShopEligible,'') <> 'E'
                       AND UPPER(ISNULL(w.PalletCategory,'')) = UPPER(@pc)
                     GROUP BY ISNULL(id.Division, '(no division)'),
                              CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END,
                              YEAR(w.LPMDt), MONTH(w.LPMDt)
                    HAVING SUM(CAST(ISNULL(w.Qty,0) AS bigint)) <> 0;";
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@pc", pc));
                cmd.CommandTimeout = 300;
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    rows.Add(new LpmWhStockCell(
                        Country:  country,
                        Division: rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        Season:   rdr.IsDBNull(1) ? "" : rdr.GetString(1),
                        Year:     rdr.IsDBNull(2) ? 0  : rdr.GetInt32(2),
                        Month:    rdr.IsDBNull(3) ? 0  : rdr.GetInt32(3),
                        Qty:      rdr.IsDBNull(4) ? 0L : rdr.GetInt64(4)));
                }
            }
            catch { /* one country unreadable — skip */ }
        }
        return rows;
    }

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

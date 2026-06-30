using System.Text;
using Wms.Data.Configuration;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Ported from LPMSIM (LpmSim.Data.Warehouse.WarehouseQueryService).
/// Backs the Warehouse Boxes report — dropdown lookups, the per-box detail
/// query, and the three group-by summaries (Division / Department / Brand).
/// Uses the same OnPremBackup connection as the other ported reports;
/// queries are 3-part-named to bfldata, Datareporting, racks.
/// </summary>
public class WarehouseBoxesService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 240;

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

    private async Task<string> ResolveSourceAsync(string? country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
            return WhBoxItemsSource.UaeSource;
        await using var conn = OpenOnPremBackup();
        return await WhBoxItemsSource.ResolveAsync(conn, country, ct);
    }

    // ============================== Dropdown helpers ==============================

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        var list = await ReadStringsAsync(@"
            SELECT DISTINCT SIMCountry
              FROM bfldata.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
             ORDER BY SIMCountry", ct);
        if (!list.Contains("UAE", StringComparer.OrdinalIgnoreCase)) list.Insert(0, "UAE");
        return list;
    }

    public async Task<List<string>> GetWarehousesAsync(string country, CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        var list = await ReadStringsAsync($@"
            SELECT DISTINCT Warehouse FROM {src}
             WHERE Warehouse IS NOT NULL AND Warehouse <> ''
             ORDER BY Warehouse", ct);
        list.Add("(blank)");
        return list;
    }

    public async Task<List<string>> GetLpmsAsync(string country, CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        return await ReadStringsAsync($@"
            SELECT DISTINCT LPM FROM {src}
             WHERE LPM IS NOT NULL AND LPM <> ''
             ORDER BY LPM", ct);
    }

    public async Task<List<string>> GetContNosAsync(string country, CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        return await ReadStringsAsync($@"
            SELECT DISTINCT ContNo FROM {src}
             WHERE ContNo IS NOT NULL AND ContNo <> ''
             ORDER BY ContNo", ct);
    }

    public Task<List<string>> GetTypeNamesAsync(CancellationToken ct = default) =>
        ReadStringsAsync(@"
            SELECT DISTINCT TypeName FROM bfldata.dbo.pallettype
             WHERE TypeName IS NOT NULL AND TypeName <> ''
             ORDER BY TypeName", ct);

    public Task<List<string>> GetPalletCategoriesAsync(CancellationToken ct = default) =>
        ReadStringsAsync(@"
            SELECT DISTINCT PalletCategory FROM bfldata.dbo.pallettype
             WHERE PalletCategory IS NOT NULL AND PalletCategory <> ''
             ORDER BY PalletCategory", ct);

    public Task<List<string>> GetDivisionsAsync(CancellationToken ct = default) =>
        ReadStringsAsync(@"
            SELECT DISTINCT Division FROM Datareporting.dbo.subclassmaster
             WHERE Division IS NOT NULL AND Division <> ''
             ORDER BY Division", ct);

    public Task<List<string>> GetDepartmentsAsync(CancellationToken ct = default) =>
        ReadStringsAsync(@"
            SELECT DISTINCT Department FROM Datareporting.dbo.subclassmaster
             WHERE Department IS NOT NULL AND Department <> ''
             ORDER BY Department", ct);

    public async Task<List<string>> GetSeasonsAsync(CancellationToken ct = default)
    {
        var list = await ReadStringsAsync(@"
            SELECT DISTINCT Season FROM bfldata.dbo.pallettype
             WHERE Season IS NOT NULL AND Season <> ''
             ORDER BY Season", ct);
        list.Add("(blank)");
        return list;
    }

    public async Task<List<string>> GetBrandsAsync(string country, CancellationToken ct = default)
    {
        var src = await ResolveSourceAsync(country, ct);
        return await ReadStringsAsync($@"
            SELECT DISTINCT Brand FROM {src}
             WHERE Brand IS NOT NULL AND Brand <> ''
             ORDER BY Brand", ct);
    }

    private async Task<List<string>> ReadStringsAsync(string sql, CancellationToken ct)
    {
        await using var conn = OpenOnPremBackup();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        var list = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            if (!rdr.IsDBNull(0)) list.Add(rdr.GetString(0));
        return list;
    }

    // ============================== Box detail query ==============================

    public async Task<List<WhBoxRow>> GetBoxesAsync(WhBoxFilter filter, int top, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var src     = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);
        var country = string.IsNullOrWhiteSpace(filter.Country) ? "UAE" : filter.Country!;
        var (whereExtra, havingExtra, parms) = BuildFilterClauses(filter, divDeptInHaving: true);

        var sql = $@"
            SELECT TOP (@top)
                   @country AS Country,
                   w.Warehouse,
                   w.PalletNo,
                   w.BoxNo,
                   w.PalletType,
                   pt.TypeName,
                   pt.PalletCategory,
                   SUM(CAST(w.Qty AS bigint))                                              AS Qty,
                   MAX(w.LPM)                                                              AS LPM,
                   MAX(scm.Division)                                                       AS Division,
                   MAX(scm.Department)                                                     AS Department,
                   MAX(w.Brand)                                                            AS Brand,
                   MAX(w.Rack)                                                             AS Rack,
                   MAX(CASE WHEN w.ShopEligible = 'E' THEN 'N' ELSE NULL END)              AS Purchased,
                   MAX(w.ContNo)                                                           AS ContNo,
                   MAX(w.TrnDate)                                                          AS TrnDate,
                   MAX(w.CurrDate)                                                         AS CurrDate,
                   SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 0 ELSE CAST(w.Qty AS bigint) END) AS SummerQty,
                   SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN CAST(w.Qty AS bigint) ELSE 0 END) AS WinterQty,
                   MAX(CAST(w.OraPoNo AS varchar(100)))                                    AS OraPoNo
              FROM {src} w
              LEFT JOIN bfldata.dbo.pallettype pt ON pt.PalletType = w.PalletType
              OUTER APPLY (
                  SELECT TOP 1 sm.Division, sm.Department
                    FROM Datareporting.dbo.upc_subclass    u
                    INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID = u.MH4ID
                   WHERE u.itemcode = w.ItemCode
                   ORDER BY sm.Division
              ) scm
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY w.Warehouse, w.PalletNo, w.BoxNo, w.PalletType, pt.TypeName, pt.PalletCategory
            HAVING 1 = 1 {havingExtra}
               AND (@mixedSeasonOnly = 0
                    OR (SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 0 ELSE CAST(w.Qty AS bigint) END) > 0
                    AND SUM(CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN CAST(w.Qty AS bigint) ELSE 0 END) > 0))
             ORDER BY w.Warehouse, w.PalletNo, w.BoxNo";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var p in parms) cmd.Parameters.Add(p);
        cmd.Parameters.Add(new SqlParameter("@top",             top));
        cmd.Parameters.Add(new SqlParameter("@country",         country));
        cmd.Parameters.Add(new SqlParameter("@mixedSeasonOnly", filter.MixedSeasonOnly ? 1 : 0));

        var rows = new List<WhBoxRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new WhBoxRow(
                Country:        rdr.GetString(0),
                Warehouse:      rdr.IsDBNull(1)  ? "" : rdr.GetString(1),
                PalletNo:       rdr.IsDBNull(2)  ? "" : rdr.GetString(2),
                BoxNo:          rdr.IsDBNull(3)  ? "" : rdr.GetString(3),
                PalletType:     rdr.IsDBNull(4)  ? "" : rdr.GetString(4),
                TypeName:       rdr.IsDBNull(5)  ? null : rdr.GetString(5),
                PalletCategory: rdr.IsDBNull(6)  ? null : rdr.GetString(6),
                Qty:            rdr.IsDBNull(7)  ? 0 : rdr.GetInt64(7),
                LPM:            rdr.IsDBNull(8)  ? null : rdr.GetString(8),
                Division:       rdr.IsDBNull(9)  ? null : rdr.GetString(9),
                Department:     rdr.IsDBNull(10) ? null : rdr.GetString(10),
                Brand:          rdr.IsDBNull(11) ? null : rdr.GetString(11),
                Rack:           rdr.IsDBNull(12) ? null : rdr.GetString(12),
                Purchased:      rdr.IsDBNull(13) ? null : rdr.GetString(13),
                ContNo:         rdr.IsDBNull(14) ? null : rdr.GetString(14),
                TrnDate:        rdr.IsDBNull(15) ? null : rdr.GetDateTime(15),
                CurrDate:       rdr.IsDBNull(16) ? null : rdr.GetDateTime(16),
                SummerQty:      rdr.IsDBNull(17) ? 0 : rdr.GetInt64(17),
                WinterQty:      rdr.IsDBNull(18) ? 0 : rdr.GetInt64(18),
                OraPoNo:        rdr.IsDBNull(19) ? null : rdr.GetString(19)));
        }
        return rows;
    }

    // ============================== Summary queries (3 modes) ==============================

    public async Task<List<WhDivisionRow>> GetDivisionSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);
        var (whereExtra, _, parms) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            {SummarySelect("div", src, whereExtra)}
             GROUP BY sm.Division
             ORDER BY sm.Division";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var p in parms) cmd.Parameters.Add(p);
        var rows = new List<WhDivisionRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            rows.Add(new WhDivisionRow(
                Division:      rdr.IsDBNull(0) ? null : rdr.GetString(0),
                LPMCurrentQty: rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                LPMFutureQty:  rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2),
                NonLPMQty:     rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3)));
        return rows;
    }

    public async Task<List<WhDepartmentRow>> GetDepartmentSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);
        var (whereExtra, _, parms) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            {SummarySelect("dept", src, whereExtra)}
             GROUP BY sm.Division, sm.Department
             ORDER BY sm.Division, sm.Department";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var p in parms) cmd.Parameters.Add(p);
        var rows = new List<WhDepartmentRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            rows.Add(new WhDepartmentRow(
                Division:      rdr.IsDBNull(0) ? null : rdr.GetString(0),
                Department:    rdr.IsDBNull(1) ? null : rdr.GetString(1),
                LPMCurrentQty: rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2),
                LPMFutureQty:  rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3),
                NonLPMQty:     rdr.IsDBNull(4) ? 0 : rdr.GetInt64(4)));
        return rows;
    }

    public async Task<List<WhBrandRow>> GetBrandSummaryAsync(WhBoxFilter filter, CancellationToken ct = default)
    {
        await using var conn = OpenOnPremBackup();
        var src = await WhBoxItemsSource.ResolveAsync(conn, filter.Country, ct);
        var (whereExtra, _, parms) = BuildFilterClauses(filter, divDeptInHaving: false);

        var sql = $@"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            {SummarySelect("brand", src, whereExtra)}
             GROUP BY sm.Division, sm.Department, w.Brand
             ORDER BY sm.Division, sm.Department, w.Brand";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var p in parms) cmd.Parameters.Add(p);
        var rows = new List<WhBrandRow>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            rows.Add(new WhBrandRow(
                Division:      rdr.IsDBNull(0) ? null : rdr.GetString(0),
                Department:    rdr.IsDBNull(1) ? null : rdr.GetString(1),
                Brand:         rdr.IsDBNull(2) ? null : rdr.GetString(2),
                LPMCurrentQty: rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3),
                LPMFutureQty:  rdr.IsDBNull(4) ? 0 : rdr.GetInt64(4),
                NonLPMQty:     rdr.IsDBNull(5) ? 0 : rdr.GetInt64(5)));
        return rows;
    }

    private static string SummarySelect(string level, string src, string whereExtra)
    {
        var selectCols = level switch
        {
            "div"   => "sm.Division",
            "dept"  => "sm.Division, sm.Department",
            "brand" => "sm.Division, sm.Department, w.Brand AS Brand",
            _       => "sm.Division",
        };
        return $@"
            SELECT {selectCols},
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                   SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                   SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty, 0) AS bigint) ELSE 0 END) AS NonLPMQty
              FROM {src} w
              INNER JOIN bfldata.dbo.pallettype          pt ON pt.PalletType = w.PalletType
              INNER JOIN Datareporting.dbo.upc_subclass    u ON u.itemcode    = w.ItemCode
              INNER JOIN Datareporting.dbo.subclassmaster sm ON sm.MH4ID      = u.MH4ID
             WHERE 1 = 1
               {whereExtra}
               AND (@lpmStatus = 0
                    OR (@lpmStatus = 1 AND w.LPMDt IS NOT NULL)
                    OR (@lpmStatus = 2 AND w.LPMDt IS NULL))
               AND (@search IS NULL OR w.PalletNo LIKE @searchLike OR w.BoxNo LIKE @searchLike)
               AND (@includeNonPurchased = 1 OR w.ShopEligible IS NULL OR w.ShopEligible <> 'E')";
    }

    // ============================== Country summary ==============================

    public async Task<List<CountrySummaryRow>> GetCountrySummaryAsync(CancellationToken ct = default)
    {
        var result = new List<CountrySummaryRow>();
        await using var conn = OpenOnPremBackup();
        var countries = new List<string>();
        await using (var cc = new SqlCommand(@"
            SELECT DISTINCT SIMCountry FROM bfldata.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
             ORDER BY SIMCountry", conn) { CommandTimeout = 60 })
        await using (var rdr = await cc.ExecuteReaderAsync(ct))
            while (await rdr.ReadAsync(ct)) countries.Add(rdr.GetString(0));

        const string sqlTemplate = @"
            DECLARE @nextMonthStart date = DATEADD(MONTH, 1, DATEFROMPARTS(YEAR(DATEADD(hour, 4, SYSUTCDATETIME())), MONTH(DATEADD(hour, 4, SYSUTCDATETIME())), 1));
            SELECT
                CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END                                        AS Season,
                SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt <  @nextMonthStart THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LPMCurrentQty,
                SUM(CASE WHEN w.LPMDt IS NOT NULL AND w.LPMDt >= @nextMonthStart THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS LPMFutureQty,
                SUM(CASE WHEN w.LPMDt IS NULL                                    THEN CAST(ISNULL(w.Qty,0) AS bigint) ELSE 0 END) AS NonLPMQty,
                SUM(CAST(ISNULL(w.Qty,0) AS bigint))                                                                              AS TotalQty
              FROM {SRC} w
             WHERE w.PalletCategory = 'ELIGIBLE'
               AND (w.ShopEligible IS NULL OR w.ShopEligible <> 'E')
             GROUP BY CASE WHEN UPPER(ISNULL(w.Season,'')) = 'W' THEN 'Winter' ELSE 'Summer' END";

        foreach (var country in countries)
        {
            try
            {
                var src = await WhBoxItemsSource.ResolveAsync(conn, country, ct);
                await using var cmd = new SqlCommand(sqlTemplate.Replace("{SRC}", src), conn) { CommandTimeout = 180 };
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    result.Add(new CountrySummaryRow(
                        Country:       country,
                        Season:        rdr.IsDBNull(0) ? "" : rdr.GetString(0),
                        LPMCurrentQty: rdr.IsDBNull(1) ? 0 : rdr.GetInt64(1),
                        LPMFutureQty:  rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2),
                        NonLPMQty:     rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3),
                        TotalQty:      rdr.IsDBNull(4) ? 0 : rdr.GetInt64(4)));
                }
            }
            catch { /* skip unresolvable / unreadable country */ }
        }
        return result;
    }

    // ============================== Filter-clause builder ==============================

    private static (string fragment, List<SqlParameter> parameters)
        BuildInClause(string colExpr, IReadOnlyList<string>? values, string prefix)
    {
        if (values is null || values.Count == 0) return ("", new());
        var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v))
                             .Select(v => v.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return ("", new());
        var names = new List<string>(distinct.Count);
        var parms = new List<SqlParameter>(distinct.Count);
        for (int i = 0; i < distinct.Count; i++)
        {
            var name = $"@{prefix}{i}";
            names.Add(name);
            parms.Add(new SqlParameter(name, distinct[i]));
        }
        return ($" AND {colExpr} IN ({string.Join(", ", names)})", parms);
    }

    private static (string where, string having, List<SqlParameter> parms)
        BuildFilterClauses(WhBoxFilter filter, bool divDeptInHaving)
    {
        var w = new StringBuilder();
        var h = new StringBuilder();
        var parms = new List<SqlParameter>();

        void AppendWhere(string col, IReadOnlyList<string>? values, string prefix)
        {
            var (frag, p) = BuildInClause(col, values, prefix);
            w.Append(frag); parms.AddRange(p);
        }

        // Warehouse: "(blank)" sentinel maps to NULL/empty.
        if (filter.Warehouse is { Count: > 0 })
        {
            var hasBlank = filter.Warehouse.Any(s => s.Equals("(blank)", StringComparison.OrdinalIgnoreCase));
            var nonBlank = filter.Warehouse
                .Where(s => !s.Equals("(blank)", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (nonBlank.Count > 0 && hasBlank)
            {
                var names = nonBlank.Select((_, i) => $"@wh{i}").ToList();
                for (int i = 0; i < nonBlank.Count; i++)
                    parms.Add(new SqlParameter($"@wh{i}", nonBlank[i]));
                w.Append($" AND (w.Warehouse IN ({string.Join(", ", names)}) OR ISNULL(w.Warehouse,'') = '')");
            }
            else if (hasBlank) w.Append(" AND ISNULL(w.Warehouse,'') = ''");
            else AppendWhere("w.Warehouse", nonBlank, "wh");
        }
        AppendWhere("pt.TypeName",       filter.TypeName,       "tn");
        AppendWhere("pt.PalletCategory", filter.PalletCategory, "pc");
        AppendWhere("w.LPM",             filter.Lpm,            "lpm");
        AppendWhere("w.Brand",           filter.Brand,          "br");
        AppendWhere("w.ContNo",          filter.ContNo,         "co");

        if (filter.Season is { Count: > 0 })
        {
            var hasBlank = filter.Season.Any(s => s.Equals("(blank)", StringComparison.OrdinalIgnoreCase));
            var nonBlank = filter.Season
                .Where(s => !s.Equals("(blank)", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (nonBlank.Count > 0 && hasBlank)
            {
                var names = nonBlank.Select((_, i) => $"@sea{i}").ToList();
                for (int i = 0; i < nonBlank.Count; i++)
                    parms.Add(new SqlParameter($"@sea{i}", nonBlank[i]));
                w.Append($" AND (pt.Season IN ({string.Join(", ", names)}) OR ISNULL(pt.Season,'') = '')");
            }
            else if (hasBlank) w.Append(" AND ISNULL(pt.Season,'') = ''");
            else if (nonBlank.Count > 0) AppendWhere("pt.Season", nonBlank, "sea");
        }

        if (divDeptInHaving)
        {
            var (divFrag, divP)   = BuildInClause("MAX(scm.Division)",   filter.Division,   "div");
            var (deptFrag, deptP) = BuildInClause("MAX(scm.Department)", filter.Department, "dept");
            h.Append(divFrag); h.Append(deptFrag);
            parms.AddRange(divP); parms.AddRange(deptP);
        }
        else
        {
            AppendWhere("sm.Division",   filter.Division,   "div");
            AppendWhere("sm.Department", filter.Department, "dept");
        }

        if (filter.TrnDateFrom is not null)
        {
            w.Append(" AND w.TrnDate >= @trnDateFrom");
            parms.Add(new SqlParameter("@trnDateFrom", filter.TrnDateFrom.Value.Date));
        }
        if (filter.TrnDateTo is not null)
        {
            w.Append(" AND w.TrnDate <= @trnDateTo");
            parms.Add(new SqlParameter("@trnDateTo", filter.TrnDateTo.Value.Date));
        }

        parms.Add(new SqlParameter("@lpmStatus",  (int)filter.LpmStatus));
        parms.Add(new SqlParameter("@search",     (object?)filter.Search ?? DBNull.Value));
        parms.Add(new SqlParameter("@searchLike",
            string.IsNullOrWhiteSpace(filter.Search) ? DBNull.Value : (object)$"%{filter.Search}%"));
        parms.Add(new SqlParameter("@includeNonPurchased", filter.IncludeNonPurchased ? 1 : 0));

        return (w.ToString(), h.ToString(), parms);
    }
}

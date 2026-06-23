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

    private SqlConnection OpenCountry(string country)
    {
        var c = new SqlConnection(WithConnectTimeout(resolver.GetCountryConnectionString(country)));
        c.Open();
        return c;
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPremBackup();
        var rows = await c.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Simcountry FROM bfldata.dbo.datasettings WHERE Simcountry IS NOT NULL ORDER BY Simcountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    /// <summary>Box Summary — one row per (Palletno, Trndate, closedby) with miss + excess totals.</summary>
    public async Task<List<BoxSummaryRow>> BoxSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxSummaryRow>(new CommandDefinition(@"
            SELECT
                Palletno   AS BoxNo,
                Trndate    AS ClosedDt,
                closedby   AS ClosedBy,
                ISNULL(SUM(missqty), 0) AS MissQty,
                ISNULL(SUM(zeroqty), 0) AS ExcessQty
            FROM bfldata.dbo.CloseR1pallet WITH (NOLOCK)
            WHERE Trndate >= @from AND Trndate <= @to
              AND ISNULL(missqty,0) + ISNULL(zeroqty,0) > 0
              AND Palletno IN (
                  SELECT contno FROM usa.dbo.AMEChecking WITH (NOLOCK) WHERE Trndate >= @from
              )
            GROUP BY Palletno, Trndate, closedby
            ORDER BY closedby DESC, Trndate DESC",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Detail Missing — items where QtyIssued &lt; Qty and Status is blank.</summary>
    public async Task<List<BoxDetailRow>> BoxDetailMissingAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxDetailRow>(new CommandDefinition(@"
            SELECT
                d.BoxNo       AS BoxNo,
                d.preparedby  AS PreparedBy,
                d.itemcode    AS ItemCode,
                d.qty         AS Qty,
                d.QtyIssued   AS QtyIssued,
                (d.qty - d.QtyIssued) AS Diff
            FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
            WHERE d.BoxNo IN (
                SELECT Palletno FROM bfldata.dbo.CloseR1pallet WITH (NOLOCK)
                WHERE Trndate >= @from AND Trndate <= @to
                  AND ISNULL(missqty,0) + ISNULL(zeroqty,0) > 0
                  AND Palletno IN (SELECT contno FROM usa.dbo.AMEChecking WITH (NOLOCK) WHERE Trndate >= @from)
            )
              AND d.QtyIssued < d.qty
              AND ISNULL(d.Status, '') = ''
            ORDER BY d.BoxNo, d.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Box Detail Excess — items where QtyIssued differs from Qty and Status is non-blank.</summary>
    public async Task<List<BoxDetailRow>> BoxDetailExcessAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<BoxDetailRow>(new CommandDefinition(@"
            SELECT
                d.BoxNo       AS BoxNo,
                d.preparedby  AS PreparedBy,
                d.itemcode    AS ItemCode,
                d.qty         AS Qty,
                d.QtyIssued   AS QtyIssued,
                (d.QtyIssued - d.qty) AS Diff
            FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
            WHERE d.BoxNo IN (
                SELECT Palletno FROM bfldata.dbo.CloseR1pallet WITH (NOLOCK)
                WHERE Trndate >= @from AND Trndate <= @to
                  AND ISNULL(missqty,0) + ISNULL(zeroqty,0) > 0
                  AND Palletno IN (SELECT contno FROM usa.dbo.AMEChecking WITH (NOLOCK) WHERE Trndate >= @from)
            )
              AND ISNULL(d.Status, '') <> ''
            ORDER BY d.BoxNo, d.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>
    /// Item Summary — per ItemCode totals of missing + excess, joined with item name
    /// (hodata.itemmaster), hierarchy/division/department (datareporting.vupc_subclass),
    /// and HO stock (racks.lpm_locstock).
    /// </summary>
    public async Task<List<ItemSummaryReportRow>> ItemSummaryAsync(string country, DateTime fromDt, DateTime toDt, CancellationToken ct = default)
    {
        await using var c = OpenCountry(country);
        var rows = await c.QueryAsync<ItemSummaryReportRow>(new CommandDefinition(@"
            ;WITH base AS (
                SELECT d.itemcode,
                       CASE WHEN ISNULL(d.Status,'') = '' AND d.QtyIssued < d.qty
                            THEN (d.qty - d.QtyIssued) ELSE 0 END AS MissingQty,
                       CASE WHEN ISNULL(d.Status,'') <> ''
                            THEN (d.QtyIssued - d.qty) ELSE 0 END AS ExcessQty
                FROM usa.dbo.vUPCBoxDet d WITH (NOLOCK)
                WHERE d.BoxNo IN (
                    SELECT Palletno FROM bfldata.dbo.CloseR1pallet WITH (NOLOCK)
                    WHERE Trndate >= @from AND Trndate <= @to
                      AND ISNULL(missqty,0) + ISNULL(zeroqty,0) > 0
                      AND Palletno IN (SELECT contno FROM usa.dbo.AMEChecking WITH (NOLOCK) WHERE Trndate >= @from)
                )
            ), agg AS (
                SELECT itemcode,
                       SUM(MissingQty) AS MissingQty,
                       SUM(ExcessQty)  AS ExcessQty
                FROM base
                GROUP BY itemcode
                HAVING SUM(MissingQty) + SUM(ExcessQty) > 0
            )
            SELECT
                a.itemcode                             AS ItemCode,
                im.description                         AS ItemName,
                sub.Heirarchy                          AS Hierarchy,
                sub.Division                           AS Division,
                sub.Department                         AS Department,
                a.MissingQty                           AS MissingQty,
                a.ExcessQty                            AS ExcessQty,
                ISNULL((SELECT SUM(soh) FROM racks.dbo.lpm_locstock WITH (NOLOCK) WHERE itemcode = a.itemcode), 0) AS HOStock
            FROM agg a
            LEFT JOIN hodata.dbo.itemmaster              im  WITH (NOLOCK) ON im.itemcode  = a.itemcode
            LEFT JOIN datareporting.dbo.vupc_subclass    sub WITH (NOLOCK) ON sub.itemcode = a.itemcode
            ORDER BY a.itemcode",
            new { from = fromDt, to = toDt }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }
}

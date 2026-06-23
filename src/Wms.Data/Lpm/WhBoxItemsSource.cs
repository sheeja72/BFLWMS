using System.Data.Common;
using System.Text.RegularExpressions;

namespace Wms.Data.Lpm;

/// <summary>
/// Ported from LPMSIM (LpmSim.Data.Warehouse.WhBoxItemsSource).
/// Resolves the warehouse-boxes table reference per country:
/// UAE   → racks.dbo.whboxitems
/// other → [&lt;DataName&gt;].dbo.WHBoxItemsExport
/// where DataName comes from bfldata.dbo.DataSettings (looked up via SIMCountry).
/// All country DBs live on the same SQL server, so cross-DB references work.
/// </summary>
public static class WhBoxItemsSource
{
    public const string UaeSource = "racks.dbo.whboxitems";

    public static async Task<string> ResolveAsync(
        DbConnection conn, string? country, CancellationToken ct = default)
    {
        var dataName = await ResolveDataNameAsync(conn, country, ct);
        return dataName is null ? UaeSource : $"[{dataName}].dbo.WHBoxItemsExport";
    }

    public static async Task<string?> ResolveDataNameAsync(
        DbConnection conn, string? country, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(country) ||
            country.Equals("UAE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? dataName;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 1 DataName
                  FROM bfldata.dbo.DataSettings
                 WHERE SIMCountry = @c
                   AND DataName IS NOT NULL
                   AND LTRIM(RTRIM(DataName)) <> '';";
            var p = cmd.CreateParameter();
            p.ParameterName = "@c";
            p.Value         = country;
            cmd.Parameters.Add(p);
            cmd.CommandTimeout = 30;
            var v = await cmd.ExecuteScalarAsync(ct);
            dataName = v is string s ? s.Trim() : null;
        }

        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName configured in bfldata.dbo.DataSettings for SIMCountry '{country}'.");

        // Sanitize: identifiers must be alnum/underscore — defend against ETL drift.
        if (!Regex.IsMatch(dataName, @"^[A-Za-z0-9_]+$"))
            throw new InvalidOperationException(
                $"Invalid DataName '{dataName}' for SIMCountry '{country}' — must be alphanumeric/underscore only.");

        return dataName;
    }
}

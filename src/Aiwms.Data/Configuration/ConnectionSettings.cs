using Microsoft.Extensions.Configuration;

namespace Aiwms.Data.Configuration;

/// <summary>
/// Resolves connection strings the AIWMS app uses at runtime. Three kinds:
///
///   1. Azure SQL AIWMS — the app's own DB on bfl-wms-sql. Connected via
///      Managed Identity (App Service) or AAD Default (local dev). NO password.
///   2. Per-country on-prem MSSQL — one connection string per country, configured
///      in Azure App Service > Configuration > Connection strings under the key
///      "{Country}_DB_ConnectionString" (LPMSIM pattern).
///   3. OnPremBackupDB — single UAE-only backup DB hosting contreceipt, upc_subclass,
///      SubclassMaster. Key: "OnPremBackupDB_ConnectionString".
/// </summary>
public interface IOnPremConnectionResolver
{
    string GetAiwmsAzureConnectionString();
    string GetCountryConnectionString(string country);
    string GetOnPremBackupConnectionString();
    IReadOnlyList<string> GetConfiguredCountries();
}

public class OnPremConnectionResolver(IConfiguration cfg) : IOnPremConnectionResolver
{
    private static readonly string[] _knownCountries =
        ["UAE", "KSA", "Kuwait", "Bahrain", "Qatar", "Oman", "Egypt"];

    public string GetAiwmsAzureConnectionString() =>
        cfg.GetConnectionString("WmsAzure")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:WmsAzure is not configured. Add it to appsettings.json or App Service configuration.");

    public string GetCountryConnectionString(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            throw new ArgumentException("Country is required.", nameof(country));
        var key = $"{country}_DB_ConnectionString";
        var cs = cfg.GetConnectionString(key);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"ConnectionStrings:{key} is not configured for country '{country}'.");
        return cs;
    }

    public string GetOnPremBackupConnectionString() =>
        cfg.GetConnectionString("OnPremBackupDB_ConnectionString")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:OnPremBackupDB_ConnectionString is not configured.");

    public IReadOnlyList<string> GetConfiguredCountries() =>
        _knownCountries
            .Where(c => !string.IsNullOrWhiteSpace(cfg.GetConnectionString($"{c}_DB_ConnectionString")))
            .ToList();
}

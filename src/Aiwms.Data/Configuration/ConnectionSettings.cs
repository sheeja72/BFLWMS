using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;

namespace Aiwms.Data.Configuration;

/// <summary>
/// Plain shape of the connection settings the admin enters in the UI.
/// Persisted as an encrypted JSON blob via ASP.NET Core Data Protection.
/// </summary>
public class ConnectionSettings
{
    public string Server     { get; set; } = "192.168.10.72";  // default per user
    public string? Instance  { get; set; }                      // optional, e.g. "LOGBACKUP"
    public int? Port         { get; set; }                      // optional
    public string Database   { get; set; } = "AIWMS";
    public string Username   { get; set; } = "";
    public string Password   { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    public bool Encrypt      { get; set; } = false;
    public int  ConnectTimeoutSec { get; set; } = 15;

    // Optional second SQL Server for the JAFZA Robo system (receives a copy of PhotoCheckingResult).
    // Leave blank to skip the Robo insert.
    public string? RoboServer   { get; set; }
    public string? RoboUsername { get; set; }
    public string? RoboPassword { get; set; }
    public bool HasRoboServer => !string.IsNullOrWhiteSpace(RoboServer) && !string.IsNullOrWhiteSpace(RoboUsername);

    public string BuildRobo() => new SqlConnectionStringBuilder
    {
        DataSource = RoboServer!,
        InitialCatalog = "bfldata",
        UserID = RoboUsername!,
        Password = RoboPassword ?? "",
        TrustServerCertificate = TrustServerCertificate,
        Encrypt = Encrypt,
        ConnectTimeout = ConnectTimeoutSec,
        ApplicationName = "AIWMS",
        Pooling = true,
        MaxPoolSize = 50,
    }.ConnectionString;

    public string ToDataSource()
    {
        if (!string.IsNullOrWhiteSpace(Instance))
            return $@"{Server}\{Instance}";
        if (Port is > 0)
            return $"{Server},{Port}";
        return Server;
    }

    public string Build(string? overrideDb = null) => new SqlConnectionStringBuilder
    {
        DataSource = ToDataSource(),
        InitialCatalog = overrideDb ?? Database,
        UserID = Username,
        Password = Password,
        TrustServerCertificate = TrustServerCertificate,
        Encrypt = Encrypt,
        ConnectTimeout = ConnectTimeoutSec,
        ApplicationName = "AIWMS",
        Pooling = true,
        MaxPoolSize = 200,
    }.ConnectionString;
}

/// <summary>
/// Provides the active connection string. Until the admin configures a connection,
/// IsConfigured = false and the app forces a redirect to /setup.
/// </summary>
public interface IConnectionConfig
{
    bool IsConfigured { get; }
    ConnectionSettings? Current { get; }
    string GetAiwmsConnectionString();
    string GetConnectionString(string database);
    bool HasRoboServer { get; }
    bool IsRoboFallback { get; }   // true = Robo == primary (no separate server configured)
    string GetRoboConnectionString();
    Task SaveAsync(ConnectionSettings settings, CancellationToken ct = default);
    Task<bool> TestAsync(ConnectionSettings settings, CancellationToken ct = default);
}

public class FileConnectionConfig : IConnectionConfig
{
    private const string ProtectorPurpose = "Aiwms.ConnectionSettings.v1";
    private readonly IDataProtector _protector;
    private readonly string _file;
    private ConnectionSettings? _cache;

    public FileConnectionConfig(IDataProtectionProvider dp, IHostEnvironment env)
    {
        _protector = dp.CreateProtector(ProtectorPurpose);
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "connection.protected");
        _cache = TryLoad();
    }

    public bool IsConfigured => _cache is not null && !string.IsNullOrEmpty(_cache.Server) && !string.IsNullOrEmpty(_cache.Username);

    public ConnectionSettings? Current => _cache;

    public string GetAiwmsConnectionString() =>
        (_cache ?? throw new InvalidOperationException("Connection not configured. Visit /setup.")).Build();

    public string GetConnectionString(string database) =>
        (_cache ?? throw new InvalidOperationException("Connection not configured. Visit /setup.")).Build(database);

    // True whenever the primary is configured — we always have *some* Robo endpoint (real or fallback).
    public bool HasRoboServer => IsConfigured;

    // True = no separate Robo server configured; the primary server is used as the Robo target.
    public bool IsRoboFallback => !(_cache?.HasRoboServer ?? false);

    public string GetRoboConnectionString() =>
        (_cache?.HasRoboServer == true)
            ? _cache.BuildRobo()
            : GetConnectionString("bfldata");  // fallback: same server, bfldata db

    public async Task SaveAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings);
        var blob = _protector.Protect(json);
        await File.WriteAllTextAsync(_file, blob, ct);
        _cache = settings;
    }

    public async Task<bool> TestAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        await using var con = new SqlConnection(settings.Build("master"));
        await con.OpenAsync(ct);
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is int i && i == 1;
    }

    private ConnectionSettings? TryLoad()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            var protectedBlob = File.ReadAllText(_file);
            var json = _protector.Unprotect(protectedBlob);
            return JsonSerializer.Deserialize<ConnectionSettings>(json);
        }
        catch
        {
            return null;
        }
    }
}

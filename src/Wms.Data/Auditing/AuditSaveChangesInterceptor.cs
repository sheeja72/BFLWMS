using System.Text.Json;
using Wms.Core;
using Wms.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Wms.Data.Auditing;

/// <summary>
/// Singleton-safe interceptor: resolves ICurrentUser from a fresh scope on each
/// SavingChanges call. This avoids the DI deadlock that occurred when the
/// interceptor was registered as Scoped and pulled into the DbContext factory
/// options lambda (which is invoked during claims transformation).
/// </summary>
public class AuditSaveChangesInterceptor(IServiceScopeFactory scopeFactory) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        AppendAuditRows(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AppendAuditRows(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void AppendAuditRows(DbContext? ctx)
    {
        if (ctx is null) return;
        string user = "system"; string? ip = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var cu = scope.ServiceProvider.GetService<ICurrentUser>();
            if (cu is not null) { user = cu.Name; ip = cu.ClientIp; }
        }
        catch { /* leave defaults */ }
        var now = DateTime.Now;
        var logs = new List<WmsAuditLog>();

        foreach (var entry in ctx.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is WmsAuditLog) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var entityName = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name;
            var pk = entry.Metadata.FindPrimaryKey();
            var key = pk is null ? "" :
                string.Join("|", pk.Properties.Select(p =>
                    (entry.State == EntityState.Deleted ? entry.OriginalValues[p] : entry.CurrentValues[p])?.ToString() ?? ""));

            var action = entry.State switch
            {
                EntityState.Added    => 'I',
                EntityState.Modified => 'U',
                EntityState.Deleted  => 'D',
                _                    => '?'
            };

            logs.Add(new WmsAuditLog
            {
                EntityName  = entityName,
                EntityKey   = Truncate(key, 200),
                Action      = action,
                ChangedBy   = Truncate(user, 100),
                ChangedTS   = now,
                ClientIp    = ip,
                ChangesJson = JsonSerializer.Serialize(BuildChangeMap(entry)),
            });
        }
        if (logs.Count > 0) ctx.Set<WmsAuditLog>().AddRange(logs);
    }

    private static Dictionary<string, object?> BuildChangeMap(EntityEntry entry)
    {
        var map = new Dictionary<string, object?>();
        foreach (var prop in entry.Properties)
        {
            switch (entry.State)
            {
                case EntityState.Added:    map[prop.Metadata.Name] = new { n = prop.CurrentValue }; break;
                case EntityState.Deleted:  map[prop.Metadata.Name] = new { o = prop.OriginalValue }; break;
                case EntityState.Modified: if (prop.IsModified) map[prop.Metadata.Name] = new { o = prop.OriginalValue, n = prop.CurrentValue }; break;
            }
        }
        return map;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public interface IActionLogger
{
    Task LogAsync(string entityName, string entityKey, char action, string? context, object? changes, CancellationToken ct = default);
}

public class ActionLogger(WmsDbContext db, ICurrentUser currentUser) : IActionLogger
{
    public async Task LogAsync(string entityName, string entityKey, char action, string? context, object? changes, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new WmsAuditLog
        {
            EntityName  = entityName,
            EntityKey   = entityKey,
            Action      = action,
            ChangedBy   = currentUser.Name,
            ClientIp    = currentUser.ClientIp,
            Context     = context,
            ChangedTS   = DateTime.Now,
            ChangesJson = changes is null ? null : JsonSerializer.Serialize(changes),
        });
        await db.SaveChangesAsync(ct);
    }
}

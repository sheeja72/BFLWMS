using System.Security.Claims;
using Aiwms.Data;
using Aiwms.Data.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Aiwms.Web.Auth;

public class AiwmsClaimsTransformer(IDbContextFactory<AiwmsDbContext> dbFactory, IConnectionConfig conn) : IClaimsTransformation
{
    public const string ActiveClaim = "aiwms_active";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity baseId || !baseId.IsAuthenticated) return principal;
        if (!conn.IsConfigured) return principal;

        // Resolve username defensively — WindowsIdentity exposes Name via SID, not claim.
        var name = baseId.Name
                   ?? principal.FindFirst(ClaimTypes.Name)?.Value
                   ?? principal.FindFirst(ClaimTypes.Upn)?.Value
                   ?? principal.FindFirst(ClaimTypes.WindowsAccountName)?.Value;
        if (string.IsNullOrEmpty(name)) return principal;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var db = await dbFactory.CreateDbContextAsync(cts.Token);
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Username == name && u.IsActive)
                .Select(u => new { Roles = u.UserRoles.Select(r => r.RoleCode).ToList() })
                .FirstOrDefaultAsync(cts.Token);
            if (user is null) return principal;

            // Add claims to the EXISTING identity (do NOT clone — that loses
            // WindowsIdentity's SID-derived Name property and breaks .Identity.Name).
            if (!baseId.HasClaim(c => c.Type == ActiveClaim))
                baseId.AddClaim(new Claim(ActiveClaim, "1"));

            // Ensure a Name claim exists (so any code reading via claim instead of
            // .Identity.Name still finds it after the WindowsIdentity goes through framework layers).
            if (!baseId.HasClaim(c => c.Type == ClaimTypes.Name))
                baseId.AddClaim(new Claim(ClaimTypes.Name, name));

            foreach (var role in user.Roles)
                if (!baseId.HasClaim(baseId.RoleClaimType, role))
                    baseId.AddClaim(new Claim(baseId.RoleClaimType, role));

            return principal;
        }
        catch
        {
            return principal;
        }
    }
}

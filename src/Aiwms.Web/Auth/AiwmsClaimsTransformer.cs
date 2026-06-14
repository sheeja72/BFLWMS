using System.Security.Claims;
using Aiwms.Core.Entities;
using Aiwms.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Aiwms.Web.Auth;

/// <summary>
/// Runs once per request after Entra OIDC has authenticated the user.
///
/// Pulls the email from the OIDC claims, finds/creates the matching AiwmsUser
/// row on Azure SQL, and attaches:
///   - aiwms_active = "1" (gates the RequireActiveUser policy)
///   - role claims from AiwmsUserRole
///
/// First-login auto-create mirrors the Barcode Generator pattern: if the
/// signed-in Entra user has no AiwmsUser row, we create one with the
/// configured DefaultRole. Admin can then promote them.
/// </summary>
public class AiwmsClaimsTransformer(
    IDbContextFactory<AiwmsDbContext> dbFactory,
    IConfiguration cfg,
    ILogger<AiwmsClaimsTransformer> logger) : IClaimsTransformation
{
    public const string ActiveClaim = "aiwms_active";

    private string DefaultRole => cfg["Aiwms:DefaultRole"] ?? "WHAssociate";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity baseId || !baseId.IsAuthenticated)
            return principal;

        // OIDC sign-in puts the user's UPN (e.g. sheeja@bflgroup.ae) on multiple claim
        // types depending on the IdP. Try the common ones in priority order.
        var email = principal.FindFirst("preferred_username")?.Value
                 ?? principal.FindFirst(ClaimTypes.Upn)?.Value
                 ?? principal.FindFirst(ClaimTypes.Email)?.Value
                 ?? principal.FindFirst(ClaimTypes.Name)?.Value
                 ?? baseId.Name;
        if (string.IsNullOrWhiteSpace(email))
            return principal;

        var displayName = principal.FindFirst("name")?.Value
                       ?? principal.FindFirst(ClaimTypes.GivenName)?.Value
                       ?? email.Split('@')[0];

        try
        {
            logger.LogInformation("ClaimsTransformer: resolving user '{Email}' (display '{Name}')", email, displayName);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var db = await dbFactory.CreateDbContextAsync(cts.Token);

            var user = await db.Users
                .Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.Username == email, cts.Token);
            logger.LogInformation("ClaimsTransformer: DB lookup for '{Email}' returned {Found}, roles={Roles}",
                email, user is null ? "NULL" : "FOUND",
                user is null ? "n/a" : string.Join(",", user.UserRoles.Select(r => r.RoleCode)));

            // Auto-create on first login
            if (user is null)
            {
                user = new AiwmsUser
                {
                    Username = email,
                    DisplayName = displayName,
                    Email = email,
                    IsActive = true,
                    CreatedBy = "OIDC-auto-create",
                };
                db.Users.Add(user);
                db.UserRoles.Add(new AiwmsUserRole
                {
                    Username = email,
                    RoleCode = DefaultRole,
                });
                await db.SaveChangesAsync(cts.Token);

                // Re-load with roles
                user = await db.Users
                    .Include(u => u.UserRoles)
                    .FirstAsync(u => u.Username == email, cts.Token);
            }

            if (!user.IsActive)
            {
                logger.LogWarning("ClaimsTransformer: user '{Email}' is INACTIVE — denying access", email);
                return principal;
            }

            if (!baseId.HasClaim(c => c.Type == ActiveClaim))
                baseId.AddClaim(new Claim(ActiveClaim, "1"));
            logger.LogInformation("ClaimsTransformer: user '{Email}' authorized with roles [{Roles}]",
                email, string.Join(",", user.UserRoles.Select(r => r.RoleCode)));

            if (!baseId.HasClaim(c => c.Type == ClaimTypes.Name))
                baseId.AddClaim(new Claim(ClaimTypes.Name, email));

            foreach (var ur in user.UserRoles)
            {
                if (!baseId.HasClaim(baseId.RoleClaimType, ur.RoleCode))
                    baseId.AddClaim(new Claim(baseId.RoleClaimType, ur.RoleCode));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ClaimsTransformer: failed to resolve/seed user '{Email}'", email);
        }

        return principal;
    }
}

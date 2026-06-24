using Microsoft.AspNetCore.Authorization;
using Wms.Core;

namespace Wms.Web.Auth;

/// <summary>Requirement: user can access the given menu key.</summary>
public sealed class MenuAccessRequirement(string menuKey) : IAuthorizationRequirement
{
    public string MenuKey { get; } = menuKey;
}

/// <summary>
/// Passes the requirement when the user is Admin (special bypass) OR has an
/// explicit aiwms_menu grant for this MenuKey OR is in one of the menu's
/// configured DefaultRoles.
/// </summary>
public sealed class MenuAccessHandler : AuthorizationHandler<MenuAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, MenuAccessRequirement req)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        // 1) Admin bypass.
        if (user.IsInRole(Roles.Admin)) { ctx.Succeed(req); return Task.CompletedTask; }

        // 2) Explicit menu grant via aiwms_menu claim.
        if (user.HasClaim(c => c.Type == MenuKeys.ClaimType && c.Value == req.MenuKey))
        {
            ctx.Succeed(req); return Task.CompletedTask;
        }

        // 3) Default-role match (current pre-grant behaviour).
        var entry = MenuKeys.All.FirstOrDefault(m => m.Key == req.MenuKey);
        if (entry is not null && entry.DefaultRoles.Any(user.IsInRole))
            ctx.Succeed(req);

        return Task.CompletedTask;
    }
}

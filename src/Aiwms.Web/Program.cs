using Aiwms.Core;
using Aiwms.Data;
using Aiwms.Data.Auditing;
using Aiwms.Data.Configuration;
using Aiwms.Data.Lpm;
using Aiwms.Web.Auth;
using Aiwms.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

namespace Aiwms.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Use HTTP.sys (Windows built-in) instead of Kestrel — Negotiate auth + interactive
        // Blazor + Kestrel hangs on prerender. HTTP.sys handles Windows Auth natively.
        // Loopback prefix doesn't need an admin URL ACL; LAN-wide binding (http://+:5217)
        // does need: netsh http add urlacl url=http://+:5217/ user=Everyone (run elevated).
        builder.WebHost.UseHttpSys(o =>
        {
            // Force Windows auth on every request — including /_blazor SignalR upgrade.
            // With AllowAnonymous=true, the WebSocket connected anonymously and the
            // entire circuit had an anonymous AuthState even after page-level auth.
            o.Authentication.Schemes = AuthenticationSchemes.Negotiate | AuthenticationSchemes.NTLM;
            o.Authentication.AllowAnonymous = false;
            o.UrlPrefixes.Add("http://localhost:5217");
        });

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(o => o.DetailedErrors = true);
        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
        {
            o.DetailedErrors = true;
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Components", LogLevel.Information);
        builder.Services.AddMudServices();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddDataProtection()
            .SetApplicationName("Aiwms")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

        builder.Services.AddSingleton<IConnectionConfig, FileConnectionConfig>();

        builder.Services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
        builder.Services.AddScoped<IActionLogger, ActionLogger>();
        builder.Services.AddScoped<BuildingService>();

        builder.Services.AddDbContextFactory<AiwmsDbContext>((sp, o) =>
        {
            var conn = sp.GetRequiredService<IConnectionConfig>();
            if (conn.IsConfigured)
            {
                o.UseSqlServer(conn.GetAiwmsConnectionString());
                // Interceptor is now Singleton + IServiceScopeFactory, so resolving it
                // from the factory options no longer deadlocks the claims transformer.
                o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
            }
            else
            {
                o.UseSqlServer("Server=(localdb)\\unconfigured;Database=unconfigured;Integrated Security=true");
            }
        }, ServiceLifetime.Scoped);

        builder.Services.AddAuthentication(HttpSysDefaults.AuthenticationScheme);
        builder.Services.AddScoped<IClaimsTransformation, AiwmsClaimsTransformer>();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.RequireActiveUser, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(AiwmsClaimsTransformer.ActiveClaim, "1"));
        });
        builder.Services.AddCascadingAuthenticationState();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseAntiforgery();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Run();
    }
}

using Wms.Core;
using Wms.Data;
using Wms.Data.Auditing;
using Wms.Data.Configuration;
using Wms.Data.Lpm;
using Wms.Web.Auth;
using Wms.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;

namespace Wms.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Kestrel (default) — Negotiate-on-HTTP.sys is gone. Entra OIDC works on
        // Linux App Service and locally on any OS.

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents(o => o.DetailedErrors = true);

        // Bump the SignalR receive limit so large render diffs (e.g. an allocation
        // grid with thousands of rows) don't blow up with the default 32 KB cap.
        builder.Services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o =>
        {
            o.MaximumReceiveMessageSize = 32 * 1024 * 1024; // 32 MB
        });
        builder.Services.Configure<Microsoft.AspNetCore.Components.Server.CircuitOptions>(o =>
        {
            o.DetailedErrors = true;
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore.Components", LogLevel.Information);
        builder.Services.AddMudServices();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddDataProtection()
            .SetApplicationName("Wms")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

        // Connection resolver replaces FileConnectionConfig / IConnectionConfig.
        // Reads WmsAzure + per-country + OnPremBackupDB conn strings from
        // IConfiguration (appsettings.json + App Service config + User Secrets).
        builder.Services.AddSingleton<IOnPremConnectionResolver, OnPremConnectionResolver>();

        builder.Services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
        builder.Services.AddScoped<IActionLogger, ActionLogger>();
        builder.Services.AddScoped<BuildingService>();
        builder.Services.AddScoped<ContainerAllocationService>();
        builder.Services.AddScoped<ReportsService>();
        builder.Services.AddScoped<WarehouseBoxesService>();
        builder.Services.AddScoped<MissingExcessSnapshotService>();
        builder.Services.AddHostedService<Wms.Web.Hosting.NightlyBatchService>();

        // WMS DbContext — Azure SQL via AAD (Managed Identity in App Service,
        // AAD Default locally via `az login`). NO password in code.
        builder.Services.AddDbContextFactory<WmsDbContext>((sp, o) =>
        {
            var resolver = sp.GetRequiredService<IOnPremConnectionResolver>();
            o.UseSqlServer(resolver.GetWmsAzureConnectionString());
            o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        }, ServiceLifetime.Scoped);

        // Entra OIDC — mirrors Barcode Generator's MSAL flow but using
        // Microsoft.Identity.Web (the .NET equivalent of @azure/msal-node).
        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

        // Auth cookie has a hard 24-hour lifetime from sign-in. SlidingExpiration is OFF
        // so the cookie does NOT roll on activity — users are signed in once per day
        // (morning login lasts the whole working day, then re-auth next morning).
        // Pairs with the in-browser idle timer in App.razor.
        builder.Services.Configure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            o =>
            {
                o.ExpireTimeSpan    = TimeSpan.FromHours(24);
                o.SlidingExpiration = false;
            });

        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages()
            .AddMicrosoftIdentityUI();

        builder.Services.AddScoped<IClaimsTransformation, WmsClaimsTransformer>();

        builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Wms.Web.Auth.MenuAccessHandler>();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.RequireActiveUser, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(WmsClaimsTransformer.ActiveClaim, "1"));

            // One policy per menu key: Authorize(Policy = MenuKeys.X) on pages
            // and AuthorizeView Policy="MenuKeys.X" in NavMenu both resolve here.
            foreach (var menu in MenuKeys.All)
            {
                options.AddPolicy(menu.Key, p => p
                    .RequireAuthenticatedUser()
                    .AddRequirements(new Wms.Web.Auth.MenuAccessRequirement(menu.Key)));
            }

            // All endpoints require auth by default. AllowAnonymous on the
            // OIDC sign-in/sign-out endpoints is set by Microsoft.Identity.Web.UI.
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
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
        app.UseRouting();
        app.UseAntiforgery();

        app.UseAuthentication();
        app.UseAuthorization();

        // Microsoft.Identity.Web.UI controllers + razor pages handle /MicrosoftIdentity/Account/*.
        app.MapControllers();
        app.MapRazorPages();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Run();
    }
}

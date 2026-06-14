using Aiwms.Core;
using Aiwms.Data;
using Aiwms.Data.Auditing;
using Aiwms.Data.Configuration;
using Aiwms.Data.Lpm;
using Aiwms.Web.Auth;
using Aiwms.Web.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor.Services;

namespace Aiwms.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Kestrel (default) — Negotiate-on-HTTP.sys is gone. Entra OIDC works on
        // Linux App Service and locally on any OS.

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

        // Connection resolver replaces FileConnectionConfig / IConnectionConfig.
        // Reads AiwmsAzure + per-country + OnPremBackupDB conn strings from
        // IConfiguration (appsettings.json + App Service config + User Secrets).
        builder.Services.AddSingleton<IOnPremConnectionResolver, OnPremConnectionResolver>();

        builder.Services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
        builder.Services.AddScoped<IActionLogger, ActionLogger>();
        builder.Services.AddScoped<BuildingService>();

        // AIWMS DbContext — Azure SQL via AAD (Managed Identity in App Service,
        // AAD Default locally via `az login`). NO password in code.
        builder.Services.AddDbContextFactory<AiwmsDbContext>((sp, o) =>
        {
            var resolver = sp.GetRequiredService<IOnPremConnectionResolver>();
            o.UseSqlServer(resolver.GetAiwmsAzureConnectionString());
            o.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
        }, ServiceLifetime.Scoped);

        // Entra OIDC — mirrors Barcode Generator's MSAL flow but using
        // Microsoft.Identity.Web (the .NET equivalent of @azure/msal-node).
        builder.Services
            .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

        builder.Services.AddControllersWithViews()
            .AddMicrosoftIdentityUI();

        builder.Services.AddScoped<IClaimsTransformation, AiwmsClaimsTransformer>();

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.RequireActiveUser, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(AiwmsClaimsTransformer.ActiveClaim, "1"));

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

        // Microsoft.Identity.Web.UI controllers handle /MicrosoftIdentity/Account/SignIn|SignOut.
        app.MapControllers();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        app.Run();
    }
}

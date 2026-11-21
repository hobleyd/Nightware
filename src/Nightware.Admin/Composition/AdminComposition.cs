using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Cloudflare.Administration;
using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Adapters.Dav.Administration;
using Nightware.Adapters.Subscriptions.Administration;
using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Jmap.Administration;
using Nightware.Admin.Auth;
using Nightware.Admin.Hosting;
using Nightware.Application.Administration;
using Nightware.Hosting;
using Nightware.Hosting.Http;
using Nightware.Hosting.Observability;

namespace Nightware.Admin.Composition;

/// <summary>
/// The console's composition root: the only <c>Nightware.Admin</c> code that references adapters
/// (ADR 0002 D2). It never references <c>Nightware.Api</c>.
/// </summary>
public static partial class AdminComposition
{
    public const string LivenessPath = "/healthz/live";

    /// <summary>
    /// Fixed, so the key ring keeps opening what it sealed (cookies, and entities' DNS host credentials) wherever the
    /// app is deployed; by default it would be derived from the content root.
    /// </summary>
    internal const string DataProtectionApplicationName = "Nightware.Admin";

    public static IServiceCollection AddNightwareAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddLogRedaction();
        services.AddTrustedProxies(configuration);
        services.AddCookielessHttpClients();
        services.AddMemoryCache();
        services.AddHealthChecks();

        services.AddOptions<AdminAuthOptions>()
            .Bind(configuration.GetSection(AdminAuthOptions.SectionName))
            // Compose sets Admin__AcceptedAcrValues__0 from ADMIN_REQUIRED_ACR, which is empty to turn MFA off.
            .PostConfigure(static o =>
            {
                foreach (string blank in o.AcceptedAcrValues.Where(string.IsNullOrWhiteSpace).ToList())
                {
                    o.AcceptedAcrValues.Remove(blank);
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AdminAuthOptions>, AdminAuthOptionsValidator>());
        services.AddSecurityHeaders(configuration, security => security.ContentSecurityPolicy = ContentSecurityPolicy(configuration));

        services.AddSingleton(static provider =>
        {
            AdminAuthOptions admin = provider.GetRequiredService<IOptions<AdminAuthOptions>>().Value;
            var options = new AdministrationOptions
            {
                DestructiveActionMaxAge = admin.DestructiveActionMaxAge,
                EntityAdminsMayDeleteData = admin.EntityAdminsMayDeleteData,
                GlobalAdminsAdministerServers = admin.GlobalAdminsAdministerServers,
                ActionEmailLifespan = admin.ActionEmailLifespan,
                AccessTokenLifespan = admin.AccessTokenLifespan,
                PublicHost = admin.PublicHost,
            };
            foreach (string acr in admin.AcceptedAcrValues)
            {
                options.AcceptedAcrValues.Add(acr);
            }

            return options;
        });

        services.AddAdminAuthentication();
        services.AddScoped<SessionAdminActor>();
        services.AddScoped<IAdminActor>(static provider => provider.GetRequiredService<SessionAdminActor>());
        services.AddScoped<IAuthorizationHandler, AdminRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminRequirement.PolicyName, static policy => policy.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new AdminRequirement()).Build());

        services.AddRazorPages(static pages =>
        {
            pages.Conventions.AllowAnonymousToFolder("/Account");
            pages.Conventions.ConfigureFilter(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
        });
        services.Configure<RouteOptions>(static routes => routes.LowercaseUrls = true);
        services.AddAntiforgery(static antiforgery =>
        {
            antiforgery.Cookie.Name = "__Secure-nightware.console.af";
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.TryAddSingleton<IAuditLog, LoggerAuditLog>();
        services.TryAddSingleton<IPasswordGenerator, RandomPasswordGenerator>();
        services.AddDataProtection().SetApplicationName(DataProtectionApplicationName);
        services.TryAddSingleton<ISecretSealer, DataProtectionSecretSealer>();
        services.AddAdministrationUseCases();
        services.AddConsoleBackends(configuration);
        services.AddHttpClient<IGrafanaStatus, GrafanaStatus>(static http => http.Timeout = GrafanaStatus.Timeout);
        return services;
    }

    public static WebApplication UseNightwareAdmin(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<IOptions<AdminAuthOptions>>().Value.AcceptedAcrValues.Count == 0)
        {
            LogMfaOff(app.Logger);
        }

        app.UseForwardedHeaders();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseExceptionHandler("/error");
        app.UseStatusCodePagesWithReExecute("/error", "?status={0}");
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<AdminActorMiddleware>();
        app.UseAuthorization();
        app.MapHealthChecks(LivenessPath, new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = static _ => false }).AllowAnonymous();
        app.MapRazorPages();
        return app;
    }

    /// <summary>
    /// No script at all; forms post to the console, and to Keycloak only when signing out (the end-session
    /// redirect follows a form post, which <c>form-action</c> also governs).
    /// </summary>
    internal static string ContentSecurityPolicy(IConfiguration configuration)
    {
        string formTargets = "'self'";
        if (Uri.TryCreate(configuration[$"{AdminAuthOptions.SectionName}:{nameof(AdminAuthOptions.Authority)}"], UriKind.Absolute, out Uri? authority))
        {
            formTargets += " " + authority.GetLeftPart(UriPartial.Authority);
        }

        return $"default-src 'none'; style-src 'self'; img-src 'self'; form-action {formTargets}; frame-ancestors 'none'; base-uri 'none'";
    }

    /// <summary>The ports, bound to each backend's console-only credentials (ADR 0002 D8).</summary>
    private static IServiceCollection AddConsoleBackends(this IServiceCollection services, IConfiguration configuration) =>
        services
            .AddKeycloakCore(configuration)
            .AddKeycloakAdministration()
            .AddStalwartAdministration(configuration)
            .AddNextcloudAdministration(configuration)
            .AddSubscriptionAdministration(configuration)
            .AddCloudflareAdministration(configuration)
            .AddDnsHostsAdministration(configuration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin console: multi-factor sign-in is OFF (Admin:AcceptedAcrValues / ADMIN_REQUIRED_ACR is empty). Only acceptable on a loopback development stack.")]
    private static partial void LogMfaOff(ILogger logger);
}

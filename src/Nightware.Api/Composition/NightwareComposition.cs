using Nightware.Api.Features.Files.Download;
using Nightware.Api.Features.Tasks.Upload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav;
using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Adapters.Jmap;
using Nightware.Api.Auth;
using Nightware.Api.Discovery;
using Nightware.Api.Features.Calendar;
using Nightware.Api.Features.Contacts;
using Nightware.Api.Features.Directory;
using Nightware.Api.Features.Files;
using Nightware.Api.Features.Mail;
using Nightware.Api.Features.MailboxSettings;
using Nightware.Api.Features.People;
using Nightware.Api.Features.Rooms;
using Nightware.Api.Features.Security;
using Nightware.Api.Features.Tasks;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Errors;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.Routing;
using Nightware.Api.Hosting;
using Nightware.Api.Observability;
using Nightware.Api.Throttling;
using Nightware.Application;
using Nightware.Application.Common.Identity;

namespace Nightware.Api.Composition;

/// <summary>
/// The composition root: the only Api code that references adapters. Feature phases fill their own
/// <c>Map*Endpoints</c>/<c>Add*</c> bodies and never edit this file (except to add options).
/// </summary>
public static class NightwareComposition
{
    /// <summary>Readiness: the process and every backend health check (Stalwart, Nextcloud, Keycloak).</summary>
    public const string HealthPath = "/healthz";

    /// <summary>Liveness: the process answers HTTP. No backend is consulted, so a backend outage never restarts the façade.</summary>
    public const string LivenessPath = "/healthz/live";

    public static IServiceCollection AddNightware(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.ConfigureHttpJsonOptions(static o => GraphJson.Configure(o.SerializerOptions));
        services.AddHttpHardening(configuration);
        services.AddNightwareTelemetry(configuration);

        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DeltaLinkOptions>().Bind(configuration.GetSection(DeltaLinkOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DeltaLinkOptions>, SigningKeyValidator>());

        services.AddGraphAuthentication(configuration);
        services.AddGraphRateLimiting(configuration);
        services.AddHealthChecks();

        services.AddSingleton<GraphLinkBuilder>();
        services.AddSingleton<DeltaLinkCodec>();

        // Self-authorising URLs (ADR §39): sources the middleware asks when a request has no bearer.
        services.AddSingleton<UploadUrlProtector>();
        services.AddSingleton<IRequestCredentialSource, UploadUrlCredentialSource>();
        services.AddSingleton<DownloadUrlProtector>();
        services.AddSingleton<IRequestCredentialSource, DownloadUrlCredentialSource>();
        services.TryAddScoped<IMailboxResolver, MailboxResolver>();

        services
            .AddApplication()
            .AddJmapAdapter(configuration)
            .AddDavAdapter(configuration)
            .AddDirectoryAdapter(configuration);

        // Fail fast on bad adapter configuration rather than on the first request that needs it.
        services.AddOptions<DirectoryOptions>().ValidateOnStart();
        services.AddOptions<BackendCredentialOptions>().ValidateOnStart();

        // Last: anything still unregistered answers 501.
        services.AddNotImplementedPortFallbacks();
        return services;
    }

    /// <summary>
    /// The request pipeline. Order matters: forwarded headers first (everything after sees the real
    /// client and scheme); correlation and the Graph exception envelope wrap all the rest; CORS after
    /// routing so preflights are answered before authentication; the rate limiter after
    /// authentication (it partitions by <c>sub</c>) and before the request budget, so a rejected
    /// request costs nothing; the budget innermost of the cross-cutting steps, so its 503/504 is
    /// written before <see cref="GraphExceptionMiddleware"/> could mistake a timeout for a client abort.
    /// </summary>
    public static WebApplication UseNightware(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<RequestCorrelationMiddleware>();
        app.UseMiddleware<GraphExceptionMiddleware>();
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseMiddleware<SelfAuthorisingUrlMiddleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseMiddleware<RequestBudgetMiddleware>();
        app.UseAuthorization();

        app.MapDiscovery().RequireNightMailCors();
        app.MapHealthChecks(HealthPath).AllowAnonymous();
        app.MapHealthChecks(LivenessPath, new HealthCheckOptions { Predicate = static _ => false }).AllowAnonymous();
        app.MapGraph();
        return app;
    }

    /// <summary>Maps every feature under both <c>/v1.0</c> and <c>/beta</c>, plus the unknown-route fallback.</summary>
    public static IEndpointRouteBuilder MapGraph(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        foreach (var version in GraphPaths.Versions)
        {
            var graph = endpoints.MapGroup(version);
            graph.RequireAuthorization();
            graph.RequireNightMailCors();
            graph.AddEndpointFilter<GraphBindingFilter>();
            graph
                .MapMailEndpoints()
                .MapCalendarEndpoints()
                .MapRoomsEndpoints()
                .MapDirectoryEndpoints()
                .MapContactsEndpoints()
                .MapPeopleEndpoints()
                .MapTasksEndpoints()
                .MapMailboxSettingsEndpoints()
                .MapFilesEndpoints()
                .MapSecurityEndpoints();
            foreach (var contributor in endpoints.ServiceProvider.GetServices<IGraphEndpointContributor>())
            {
                contributor.Map(graph);
            }

            endpoints.MapFallback(version + "/{**path}", UnknownGraphRoute.HandleAsync)
                .RequireAuthorization()
                .RequireNightMailCors();
        }

        return endpoints;
    }
}

/// <summary>
/// Extra endpoints resolved from DI and mapped into every Graph version group (so they get auth, the
/// binding filter and the fallback ordering). Feature code does not use this — it fills its own
/// <c>Map*Endpoints</c>; test hosts use it to add probe routes to the real <c>Program</c>.
/// </summary>
public interface IGraphEndpointContributor
{
    void Map(RouteGroupBuilder graph);
}

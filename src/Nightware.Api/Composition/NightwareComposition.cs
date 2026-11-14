using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Adapters.Dav;
using Nightware.Adapters.Directory;
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
using Nightware.Application;
using Nightware.Application.Common.Identity;

namespace Nightware.Api.Composition;

/// <summary>
/// The composition root: the only Api code that references adapters. Feature phases fill their own
/// <c>Map*Endpoints</c>/<c>Add*</c> bodies and never edit this file (except to add options).
/// </summary>
public static class NightwareComposition
{
    public const string HealthPath = "/healthz";

    public static IServiceCollection AddNightware(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.ConfigureHttpJsonOptions(static o => GraphJson.Configure(o.SerializerOptions));
        services.Configure<ForwardedHeadersOptions>(static o =>
        {
            // Nightware runs behind Caddy in its own network; trust the proxy's X-Forwarded-* headers.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedPrefix;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DeltaLinkOptions>().Bind(configuration.GetSection(DeltaLinkOptions.SectionName));

        services.AddGraphAuthentication(configuration);
        services.AddHealthChecks();

        services.AddSingleton<GraphLinkBuilder>();
        services.AddSingleton<DeltaLinkCodec>();
        services.TryAddScoped<IMailboxResolver, MailboxResolver>();

        services
            .AddApplication()
            .AddJmapAdapter(configuration)
            .AddDavAdapter(configuration)
            .AddDirectoryAdapter(configuration);

        // Last: anything still unregistered answers 501.
        services.AddNotImplementedPortFallbacks();
        return services;
    }

    public static WebApplication UseNightware(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        app.UseMiddleware<RequestCorrelationMiddleware>();
        app.UseMiddleware<GraphExceptionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapDiscovery();
        app.MapHealthChecks(HealthPath).AllowAnonymous();
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

            endpoints.MapFallback(version + "/{**path}", UnknownGraphRoute.HandleAsync).RequireAuthorization();
        }

        return endpoints;
    }
}

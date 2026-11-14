using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Calendar;

namespace Nightware.Adapters.Dav;

public static class DavAdapterServiceCollectionExtensions
{
    /// <summary>Registers the Dav adapter: core client plus every feature port it implements.</summary>
    public static IServiceCollection AddDavAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AddDavCore(services, configuration);
        AddDavCodecs(services);

        // Feature phases register their port implementations from their own folders
        // (Calendar/, Contacts/, Tasks/, Rooms/, Files/) here.
        return services;
    }

    private static void AddDavCore(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DavOptions>()
            .Bind(configuration.GetSection(DavOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DavOptions>, DavOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.TryAddSingleton<DavPaths>();
        services.TryAddTransient<DavAuthenticationHandler>();

        // Resilience is outermost so every attempt passes through the auth handler again.
        var client = services.AddHttpClient<IDavClient, DavClient>((sp, http) =>
        {
            http.BaseAddress = sp.GetRequiredService<DavPaths>().Origin;
            http.Timeout = Timeout.InfiniteTimeSpan; // the pipeline's timeouts govern
        });
        client.AddResilienceHandler(DavResilience.PipelineName, (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));
        client.AddHttpMessageHandler<DavAuthenticationHandler>();

        services.TryAddScoped<IDavPrincipalDiscovery, DavPrincipalDiscovery>();
        services.TryAddScoped<IDavCollectionCatalog, DavCollectionCatalog>();

        services.AddHttpClient(DavHealthProbe.HttpClientName);
        services.TryAddSingleton<DavHealthProbe>();
        services.AddHealthChecks().AddCheck<DavHealthCheck>(DavHealthProbe.Name, tags: [DavHealthProbe.Tag]);
    }

    private static void AddDavCodecs(IServiceCollection services)
    {
        services.TryAddSingleton<IcalNetCalendarCodec>();
        services.TryAddSingleton<IICalendarCodec>(sp => sp.GetRequiredService<IcalNetCalendarCodec>());
        services.TryAddSingleton<IcsEditor>();
        services.TryAddSingleton<FreeBusyCodec>();
        services.TryAddSingleton<VTodoCodec>();
        services.TryAddSingleton<VCardCodec>();
    }
}

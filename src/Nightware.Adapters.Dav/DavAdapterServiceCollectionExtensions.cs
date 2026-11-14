using Nightware.Adapters.Dav.Directory;
using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Contacts;
using Nightware.Adapters.Dav.Files;
using Nightware.Adapters.Dav.Rooms;
using Nightware.Adapters.Dav.Tasks;
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

        // Each feature registers its ports from its own folder, so phases never edit this file.
        return services
            .AddDavCalendar()
            .AddDavRooms()
            .AddDavContacts()
            .AddDavDirectory()
            .AddDavTasks()
            .AddDavFiles();
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
        client.ConfigurePrimaryHttpMessageHandler(CookieFreeHandler);
        client.AddResilienceHandler(DavResilience.PipelineName, (builder, context) =>
            DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<DavOptions>>().Value));
        client.AddHttpMessageHandler<DavAuthenticationHandler>();

        services.TryAddScoped<IDavPrincipalDiscovery, DavPrincipalDiscovery>();
        services.TryAddScoped<IDavCollectionCatalog, DavCollectionCatalog>();

        services.AddHttpClient(DavHealthProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(CookieFreeHandler);
        services.TryAddSingleton<DavHealthProbe>();
        services.AddHealthChecks().AddCheck<DavHealthCheck>(DavHealthProbe.Name, tags: [DavHealthProbe.Tag]);
    }

    // Pooled handlers are shared across callers. Nextcloud answers a bearer request with a session
    // cookie; with cookies on, that cookie would ride along on the next user's request (CSRF 401s at
    // best, another user's data at worst). Every DAV client must stay cookie-free.
    internal static SocketsHttpHandler CookieFreeHandler() => new() { UseCookies = false };

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

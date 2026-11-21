using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core.Http;
using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Administration;

namespace Nightware.Adapters.Dav.Administration;

public static class DavAdministrationServiceCollectionExtensions
{
    /// <summary>Resilience pipeline of the provisioning client (retries GET only, see <see cref="DavResilience"/>).</summary>
    public const string PipelineName = "nightware-nextcloud-provisioning";

    /// <summary>
    /// Registers the admin console's Nextcloud ports: <see cref="IGroupwareAccounts"/> and an
    /// <see cref="IBackendStatus"/>, bound to <c>NextcloudProvisioning:</c>. Self-contained: it needs neither
    /// <c>Dav:</c> nor <c>AddDavAdapter</c>, and the Graph façade never calls it.
    /// </summary>
    public static IServiceCollection AddNextcloudAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<NextcloudProvisioningOptions>()
            .Bind(configuration.GetSection(NextcloudProvisioningOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NextcloudProvisioningOptions>, NextcloudProvisioningOptionsValidator>());

        // Cookie-free and redirect-free: Nextcloud answers Basic with a session cookie, and an unauthenticated
        // request with a redirect to its login page. The pipeline's timeouts govern, not HttpClient.Timeout.
        services.AddHttpClient<NextcloudProvisioningClient>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
            {
                DavHandlers.DisableRedirects(handler);
                DavHandlers.DisableCookies(handler);
            })
            .AddResilienceHandler(PipelineName, static (builder, context) =>
                DavResilience.Configure(builder, context.ServiceProvider.GetRequiredService<IOptions<NextcloudProvisioningOptions>>().Value.ToResilienceOptions()));

        services.TryAddTransient<IGroupwareAccounts, NextcloudGroupwareAccounts>();
        services.TryAddTransient<IGroupwareGroups, NextcloudGroupwareGroups>();
        services.TryAddTransient<ISharedCalendars, NextcloudSharedCalendars>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IBackendStatus, NextcloudBackendStatus>());
        return services;
    }
}

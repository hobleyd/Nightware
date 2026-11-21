using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;

namespace Nightware.Adapters.Jmap.Administration;

public static class JmapAdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stalwart side of the admin console: <see cref="IMailAccounts"/>, <see cref="IMailDomains"/>,
    /// <see cref="IMailingLists"/>, <see cref="ISharedMailAccounts"/> and an <see cref="IBackendStatus"/>, over a management client with the console's own credential
    /// (<c>StalwartManagement</c>). Independent of <c>AddJmapAdapter</c>: needs no user context or backend credentials.
    /// </summary>
    public static IServiceCollection AddStalwartAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StalwartManagementOptions>()
            .Bind(configuration.GetSection(StalwartManagementOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<StalwartManagementOptions>, StalwartManagementOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<IStalwartManagementClient, StalwartManagementClient>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CookieFreeHandler)
            .AddResilienceHandler("stalwart-management", static (pipeline, context) =>
            {
                StalwartManagementOptions options = context.ServiceProvider.GetRequiredService<IOptions<StalwartManagementOptions>>().Value;
                JmapAdapterServiceCollectionExtensions.ConfigurePipeline(pipeline, options.RequestTimeout, options.AttemptTimeout, options.MaxRetryAttempts);
            });

        services.TryAddScoped<IMailAccounts, StalwartMailAccounts>();
        services.TryAddScoped<IMailDomains, StalwartMailDomains>();
        services.TryAddScoped<IMailingLists, StalwartMailingLists>();
        services.TryAddScoped<ISharedMailAccounts, StalwartSharedMailboxes>();
        services.TryAddScoped<Nightware.Application.Administration.AutoReplies.IMailboxAutoReplies, StalwartMailboxAutoReplies>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IBackendStatus, StalwartBackendStatus>());
        return services;
    }

    // Pooled handlers are shared: no cookie jar, and no redirects (.NET would drop Authorization on them anyway).
    internal static SocketsHttpHandler CookieFreeHandler() => new() { UseCookies = false, AllowAutoRedirect = false };
}
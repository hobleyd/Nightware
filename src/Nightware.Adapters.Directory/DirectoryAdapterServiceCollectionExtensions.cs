using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.MailboxSettings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory;

public static class DirectoryAdapterServiceCollectionExtensions
{
    /// <summary>Health-check tag shared by every backend check.</summary>
    public const string BackendHealthTag = "backend";

    /// <summary>Registers the Directory adapter: core client plus every feature port it implements.</summary>
    /// <remarks>
    /// Requires an <see cref="IUserContext"/> registration (scoped, from the Api). Options are
    /// validated on first use; the host may add <c>ValidateOnStart</c> to fail fast.
    /// </remarks>
    public static IServiceCollection AddDirectoryAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DirectoryOptions>().Bind(configuration.GetSection(DirectoryOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DirectoryOptions>, DirectoryOptionsValidator>());
        services.AddOptions<BackendCredentialOptions>().Bind(configuration.GetSection(BackendCredentialOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BackendCredentialOptions>, BackendCredentialOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // Core: token endpoint (singleton, owns the service-account token) and the admin REST client.
        services.AddHttpClient(KeycloakHttp.TokenClientName).AddKeycloakResilience();
        services.TryAddSingleton<IKeycloakTokenClient, KeycloakTokenClient>();
        services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>().AddKeycloakResilience();

        // Credentials: per request, because it reads the caller's IUserContext.
        services.AddScoped<IBackendCredentials, BackendCredentials>();

        services.AddHealthChecks().AddCheck<KeycloakRealmHealthCheck>(KeycloakRealmHealthCheck.Name, tags: [BackendHealthTag]);

        // Each feature registers its ports from its own folder, so phases never edit this file.
        return services
            .AddDirectoryUsers()
            .AddDirectoryPreferences();
    }
}

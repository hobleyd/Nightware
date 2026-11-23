using Nightware.Adapters.Directory.Directory;
using Nightware.Adapters.Directory.MailboxSettings;
using Nightware.Adapters.Directory.Subscriptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Application.Common.Identity;
using Nightware.Adapters.Directory.Chats;

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

        services.AddKeycloakCore(configuration);

        services.AddOptions<BackendCredentialOptions>().Bind(configuration.GetSection(BackendCredentialOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BackendCredentialOptions>, BackendCredentialOptionsValidator>());

        // Credentials: per request, because it reads the caller's IUserContext.
        services.AddScoped<IBackendCredentials, BackendCredentials>();

        // Each feature registers its ports from its own folder, so phases never edit this file.
        return services
            .AddDirectoryUsers()
            .AddDirectoryPreferences()
            .AddDirectorySubscriptions()
            .AddDirectoryChats();
    }

    /// <summary>
    /// Registers the Keycloak core only: <see cref="DirectoryOptions"/> (section <c>Directory</c>), the
    /// token client (singleton, owns the service-account token), the typed admin REST client and the realm
    /// health check. No <see cref="IUserContext"/> is needed. The admin console calls this with
    /// <c>Directory:</c> bound to its own client (<c>nightware-admin</c>, ADR 0002 D2); the Graph host gets it
    /// through <see cref="AddDirectoryAdapter"/>. Call it once per container.
    /// </summary>
    public static IServiceCollection AddKeycloakCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DirectoryOptions>().Bind(configuration.GetSection(DirectoryOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DirectoryOptions>, DirectoryOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        // Cookie-free: pooled handlers are shared across callers, so cookies must never persist.
        services.AddHttpClient(KeycloakHttp.TokenClientName).ConfigurePrimaryHttpMessageHandler(CookieFreeHandler).AddKeycloakResilience();
        services.TryAddSingleton<IKeycloakTokenClient, KeycloakTokenClient>();
        services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>().ConfigurePrimaryHttpMessageHandler(CookieFreeHandler).AddKeycloakResilience();

        services.AddHealthChecks().AddCheck<KeycloakRealmHealthCheck>(KeycloakRealmHealthCheck.Name, tags: [BackendHealthTag]);
        return services;
    }

    internal static SocketsHttpHandler CookieFreeHandler() => new() { UseCookies = false };
}

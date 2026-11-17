using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory.Tests.TestSupport;

/// <summary>The adapter's real DI graph (AddDirectoryAdapter) pointed at a <see cref="KeycloakStub"/>.</summary>
internal sealed class AdapterHost : IAsyncDisposable
{
    private AdapterHost(ServiceProvider provider, CapturingLoggerProvider logs)
    {
        Provider = provider;
        Logs = logs;
    }

    public ServiceProvider Provider { get; }

    public CapturingLoggerProvider Logs { get; }

    public FakeUserContext User => (FakeUserContext)Provider.GetRequiredService<IUserContext>();

    public static AdapterHost Create(
        string keycloakUrl,
        IDictionary<string, string?>? settings = null,
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configure = null)
    {
        Dictionary<string, string?> config = new()
        {
            ["Directory:KeycloakBaseUrl"] = keycloakUrl,
            ["Directory:Realm"] = KeycloakStub.Realm,
            ["Directory:ClientId"] = KeycloakStub.ClientId,
            ["Directory:ClientSecret"] = KeycloakStub.ClientSecret,
            ["Directory:RetryBaseDelay"] = "00:00:00.001",
            ["Directory:RequestTimeout"] = "00:00:05",
        };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        // The Api registers IUserContext per request; a singleton fake stands in.
        services.TryAddSingleton<IUserContext>(new FakeUserContext());
        services.AddDirectoryAdapter(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        configure?.Invoke(services);

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return new AdapterHost(provider, logs);
    }

    /// <summary>
    /// The admin console's DI graph: <c>AddKeycloakCore</c> + <c>AddKeycloakAdministration</c> only, bound to
    /// client <see cref="ConsoleClientId"/>, with no <see cref="IUserContext"/> registered.
    /// </summary>
    public static AdapterHost CreateConsole(string keycloakUrl, IDictionary<string, string?>? settings = null)
    {
        Dictionary<string, string?> config = new()
        {
            ["Directory:KeycloakBaseUrl"] = keycloakUrl,
            ["Directory:Realm"] = KeycloakStub.Realm,
            ["Directory:ClientId"] = ConsoleClientId,
            ["Directory:ClientSecret"] = "nightware-admin-dev-secret",
            ["Directory:RetryBaseDelay"] = "00:00:00.001",
            ["Directory:RequestTimeout"] = "00:00:05",
        };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddKeycloakCore(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        Nightware.Adapters.Directory.Administration.DirectoryAdministrationServiceCollectionExtensions.AddKeycloakAdministration(services);

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        return new AdapterHost(provider, logs);
    }

    public const string ConsoleClientId = "nightware-admin";

    private readonly List<IServiceScope> _scopes = [];

    /// <summary>Resolves <typeparamref name="T"/> from a fresh scope (the console's ports are scoped), disposed with the host.</summary>
    public T Scoped<T>()
        where T : notnull
    {
        IServiceScope scope = Provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        await Provider.DisposeAsync();
    }
}

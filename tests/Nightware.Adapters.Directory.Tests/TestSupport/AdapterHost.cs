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

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

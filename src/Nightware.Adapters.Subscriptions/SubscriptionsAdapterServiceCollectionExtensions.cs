using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Npgsql;

using Nightware.Application.Subscriptions;

namespace Nightware.Adapters.Subscriptions;

public static class SubscriptionsAdapterServiceCollectionExtensions
{
    /// <summary>
    /// The change-notification store, webhook client and notifier (ADR 0006 D10). Whether <c>Subscriptions</c> is configured is
    /// read when they are first resolved (hosts and tests layer configuration after registration); unconfigured,
    /// both answer 501 like any other unbuilt surface.
    /// </summary>
    public static IServiceCollection AddSubscriptionsAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SubscriptionOptions>().Bind(configuration.GetSection(SubscriptionOptions.SectionName));
        services.TryAddSingleton(static sp => NpgsqlDataSource.Create(Settings(sp).ConnectionString!));
        services.TryAddSingleton(static sp => new CredentialSealer(Settings(sp).SealingKey!));
        services.TryAddSingleton<SubscriptionSchema>();
        services.TryAddSingleton<PostgresSubscriptionStore>();
        services.TryAddSingleton<ISubscriptionStore>(static sp =>
            Settings(sp).IsConfigured ? sp.GetRequiredService<PostgresSubscriptionStore>() : UnconfiguredSubscriptions.Instance);
        services.TryAddScoped<WebhookNotificationEndpoint>();
        services.TryAddScoped<INotificationEndpoint>(static sp =>
            Settings(sp).IsConfigured ? sp.GetRequiredService<WebhookNotificationEndpoint>() : UnconfiguredSubscriptions.Instance);
        services.AddHostedService<SubscriptionPoller>();
        services.AddHttpClient(WebhookNotificationEndpoint.ClientName)
            .ConfigurePrimaryHttpMessageHandler(static sp =>
            {
                var origins = Settings(sp).TestOnlyAllowedOrigins.ToArray();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false,
                    AutomaticDecompression = DecompressionMethods.None,
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    ConnectCallback = (context, cancellationToken) => NotificationAddressGuard.ConnectAsync(context, origins, cancellationToken),
                };
            });
        return services;
    }

    private static SubscriptionOptions Settings(IServiceProvider services) => services.GetRequiredService<IOptions<SubscriptionOptions>>().Value;
}

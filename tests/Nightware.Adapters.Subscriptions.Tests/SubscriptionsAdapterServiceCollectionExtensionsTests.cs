using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Nightware.Adapters.Subscriptions;
using Nightware.Application.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class SubscriptionsAdapterServiceCollectionExtensionsTests
{
    private static ServiceProvider Provider(string? connectionString, string? sealingKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subscriptions:ConnectionString"] = connectionString,
            ["Subscriptions:SealingKey"] = sealingKey,
        }).Build();
        return new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddSubscriptionsAdapter(configuration).BuildServiceProvider();
    }

    [Fact]
    public void Configured_the_store_is_postgres_and_the_endpoint_is_the_webhook_client()
    {
        using var provider = Provider("Host=unused", new string('a', 64));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISubscriptionStore>().ShouldBeOfType<PostgresSubscriptionStore>();
        scope.ServiceProvider.GetRequiredService<INotificationEndpoint>().ShouldBeOfType<WebhookNotificationEndpoint>();
        provider.GetServices<IHostedService>().ShouldContain(s => s is SubscriptionPoller);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Host=unused", null)]
    [InlineData(null, "aa")]
    [InlineData(" ", "aa")]
    public void Without_both_a_connection_string_and_a_sealing_key_both_are_unconfigured(string? connectionString, string? sealingKey)
    {
        using var provider = Provider(connectionString, sealingKey);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISubscriptionStore>().ShouldBeSameAs(UnconfiguredSubscriptions.Instance);
        scope.ServiceProvider.GetRequiredService<INotificationEndpoint>().ShouldBeSameAs(UnconfiguredSubscriptions.Instance);
    }
}

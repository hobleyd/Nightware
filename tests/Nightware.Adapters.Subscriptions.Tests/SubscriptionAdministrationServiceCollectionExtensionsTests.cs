using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Nightware.Adapters.Subscriptions.Administration;
using Nightware.Application.Administration.Subscriptions;
using Nightware.Application.Subscriptions;

namespace Nightware.Adapters.Subscriptions.Tests;

public sealed class SubscriptionAdministrationServiceCollectionExtensionsTests
{
    private static ServiceProvider Provider(string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Subscriptions:ConnectionString"] = connectionString,
        }).Build();
        return new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddSubscriptionAdministration(configuration).BuildServiceProvider();
    }

    [Fact]
    public void With_only_a_connection_string_the_console_reads_postgres_and_runs_no_poller()
    {
        using var provider = Provider("Host=unused");
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChangeSubscriptions>().ShouldBeOfType<PostgresChangeSubscriptions>();
        scope.ServiceProvider.GetRequiredService<INotificationEndpoint>().ShouldBeOfType<WebhookNotificationEndpoint>();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [Fact]
    public void Without_one_both_are_unconfigured()
    {
        using var provider = Provider(null);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChangeSubscriptions>().ShouldBeOfType<UnconfiguredChangeSubscriptions>();
        scope.ServiceProvider.GetRequiredService<INotificationEndpoint>().ShouldBeOfType<UnconfiguredSubscriptions>();
    }
}

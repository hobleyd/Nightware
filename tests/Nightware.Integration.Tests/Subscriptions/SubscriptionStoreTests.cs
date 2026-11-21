using Npgsql;

using Nightware.Adapters.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Subscriptions;

/// <summary>The Postgres subscription store against a real Postgres (opt-in, like the rest of this project).</summary>
public sealed class SubscriptionStoreTests : IAsyncLifetime
{
    private SubscriptionDatabase? _database;
    private NpgsqlDataSource? _source;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (DevStack.Enabled)
        {
            _database = await SubscriptionDatabase.StartAsync(Ct);
            _source = NpgsqlDataSource.Create(_database.ConnectionString);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private static Subscription New(string creator) => new(
        Guid.NewGuid().ToString(), creator, "me/events", new SubscriptionTarget(new MailboxRef("a@example.com"), SubscriptionResourceKind.Events, null),
        SubscriptionChangeType.Created, new Uri("https://hooks.example.com/n"), new DateTimeOffset(2026, 11, 22, 1, 2, 3, TimeSpan.Zero))
    {
        ClientState = "state",
        ApplicationId = "app",
    };

    [Fact]
    public async Task Subscriptions_round_trip_per_creator_with_the_credential_sealed()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), new SubscriptionSchema(_source!));
        var mine = New("alice");
        var theirs = New("bob") with { Target = new SubscriptionTarget(new MailboxRef("b@example.com"), SubscriptionResourceKind.Tasks, "L1") };

        (await store.CreateAsync(mine, "alice-refresh-token", Ct)).IsSuccess.ShouldBeTrue();
        (await store.CreateAsync(theirs, "bob-refresh-token", Ct)).IsSuccess.ShouldBeTrue();

        (await store.GetAsync("alice", mine.Id, Ct)).Value.ShouldBe(mine);
        (await store.GetAsync("alice", theirs.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.ListAsync("bob", Ct)).Value.ShouldHaveSingleItem().ShouldBe(theirs);

        await using (var raw = _source!.CreateCommand("SELECT credential FROM nightware_subscriptions"))
        await using (var reader = await raw.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                System.Text.Encoding.UTF8.GetString((byte[])reader[0]).ShouldNotContain("refresh-token");
            }
        }

        var renewed = mine with { ExpirationDateTime = mine.ExpirationDateTime.AddDays(1), SyncState = "s1" };
        (await store.UpdateAsync(renewed, null, Ct)).IsSuccess.ShouldBeTrue();
        (await store.GetAsync("alice", mine.Id, Ct)).Value.ShouldBe(renewed);
        (await store.UpdateAsync(renewed with { CreatorId = "bob" }, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        (await store.DeleteAsync("bob", mine.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.DeleteAsync("alice", mine.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await store.ListAsync("alice", Ct)).Value.ShouldBeEmpty();
    }
}

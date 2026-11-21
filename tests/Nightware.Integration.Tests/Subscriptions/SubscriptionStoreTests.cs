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
        var mine = New("alice") with { LifecycleNotificationUrl = new Uri("https://hooks.example.com/lifecycle"), SyncState = "t0" };
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

        // A renewal changes what the client set, never the poller's sync state.
        var renewed = mine with { ExpirationDateTime = mine.ExpirationDateTime.AddDays(1), SyncState = "stale" };
        (await store.UpdateAsync(renewed, null, Ct)).Value.ShouldBe(renewed with { SyncState = mine.SyncState });
        (await store.GetAsync("alice", mine.Id, Ct)).Value.ExpirationDateTime.ShouldBe(renewed.ExpirationDateTime);
        (await store.UpdateAsync(renewed with { CreatorId = "bob" }, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        (await store.DeleteAsync("bob", mine.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.DeleteAsync("alice", mine.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await store.ListAsync("alice", Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Due_subscriptions_are_leased_once_completed_and_swept_when_expired()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), new SubscriptionSchema(_source!));
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var live = New("alice") with { ExpirationDateTime = now.AddDays(1) };
        var expired = New("alice") with { ExpirationDateTime = now.AddMinutes(-1) };
        await store.CreateAsync(live, "r1", Ct);
        await store.CreateAsync(expired, "r0", Ct);

        var leased = (await store.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldHaveSingleItem();
        leased.Subscription.Id.ShouldBe(live.Id);
        leased.RefreshToken.ShouldBe("r1");
        (await store.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldBeEmpty("a leased subscription is not leased twice");

        (await store.CompletePollAsync(live.Id, "s1", "r2", now, now.AddSeconds(10), 0, Ct)).IsSuccess.ShouldBeTrue();
        (await store.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldBeEmpty("not due again yet");
        var again = (await store.LeaseDueAsync(now.AddSeconds(11), TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldHaveSingleItem();
        again.Subscription.SyncState.ShouldBe("s1");
        again.RefreshToken.ShouldBe("r2");
        again.LastPoll.ShouldBe(now);

        (await store.CompletePollAsync(live.Id, null, null, null, now.AddSeconds(20), 1, Ct)).IsSuccess.ShouldBeTrue();
        var kept = (await store.LeaseDueAsync(now.AddSeconds(21), TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldHaveSingleItem();
        (kept.Subscription.SyncState, kept.RefreshToken, kept.Failures).ShouldBe(("s1", "r2", 1));

        (await store.RemoveExpiredAsync(now, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(expired.Id);
        (await store.GetAsync("alice", expired.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_renewal_with_a_new_credential_ends_the_backoff_at_once()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), new SubscriptionSchema(_source!));
        var sub = New("carol") with { ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(1) };
        await store.CreateAsync(sub, "r1", Ct);
        var leased = (await store.LeaseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(2), 10, Ct)).Value.Single(l => l.Subscription.Id == sub.Id);
        leased.ReauthorizationRequestedAt.ShouldBeNull();
        var asked = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        (await store.MarkReauthorizationRequestedAsync(sub.Id, asked, Ct)).IsSuccess.ShouldBeTrue();
        await store.CompletePollAsync(sub.Id, "s9", null, null, DateTimeOffset.UtcNow.AddSeconds(-1), 5, Ct);
        (await store.LeaseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(2), 10, Ct)).Value.Single(l => l.Subscription.Id == sub.Id)
            .ReauthorizationRequestedAt.ShouldBe(asked);
        await store.CompletePollAsync(sub.Id, null, null, null, DateTimeOffset.UtcNow.AddHours(1), 5, Ct);

        (await store.UpdateAsync(leased.Subscription, "r2", Ct)).IsSuccess.ShouldBeTrue();

        var again = (await store.LeaseDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), TimeSpan.FromMinutes(2), 10, Ct)).Value.Single(l => l.Subscription.Id == sub.Id);
        (again.RefreshToken, again.Failures, again.Subscription.SyncState, again.ReauthorizationRequestedAt).ShouldBe(("r2", 0, "s9", (DateTimeOffset?)null));
    }

    [Fact]
    public async Task A_credential_sealed_under_a_replaced_key_is_leased_without_one()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var schema = new SubscriptionSchema(_source!);
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var sub = New("dave") with { ExpirationDateTime = now.AddDays(1) };
        await new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), schema).CreateAsync(sub, "r1", Ct);

        var rotated = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('d', 64)), schema);

        var leased = (await rotated.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldHaveSingleItem();
        leased.Subscription.Id.ShouldBe(sub.Id);
        leased.RefreshToken.ShouldBeNull();
    }

    [Fact]
    public async Task A_lapsed_lease_is_leased_again_and_a_round_takes_at_most_max()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), new SubscriptionSchema(_source!));
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await store.CreateAsync(New("erin") with { ExpirationDateTime = now.AddDays(1) }, "r", Ct);
        }

        (await store.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 2, Ct)).Value.Count.ShouldBe(2);
        (await store.LeaseDueAsync(now, TimeSpan.FromMinutes(2), 2, Ct)).Value.Count.ShouldBe(1);
        (await store.LeaseDueAsync(now.AddMinutes(1), TimeSpan.FromMinutes(2), 10, Ct)).Value.ShouldBeEmpty("every lease is still held");
        (await store.LeaseDueAsync(now.AddMinutes(3), TimeSpan.FromMinutes(2), 10, Ct)).Value.Count.ShouldBe(3, "a poller that died leaves its leases to lapse");
    }

    [Fact]
    public async Task Completing_or_renewing_an_unknown_subscription_is_not_found()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), new SubscriptionSchema(_source!));

        (await store.CompletePollAsync("missing", "s", "r", null, DateTimeOffset.UtcNow, 0, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.UpdateAsync(New("frank"), "r", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.RemoveExpiredAsync(DateTimeOffset.UtcNow, Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Seen_items_are_seeded_looked_up_recorded_and_go_with_their_subscription()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var schema = new SubscriptionSchema(_source!);
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), schema);
        var items = new PostgresSubscriptionItems(_source!, schema);
        var sub = New("dave");
        await store.CreateAsync(sub, "r1", Ct);

        (await items.KnownAsync(sub.Id, ["a"], Ct)).Value.ShouldBeNull("not tracked until seeded");

        (await items.ReplaceAsync(sub.Id, [.. Enumerable.Range(0, 5000).Select(static i => "id-" + i)], Ct)).IsSuccess.ShouldBeTrue();
        (await items.KnownAsync(sub.Id, ["id-1", "id-4999", "new"], Ct)).Value!.ShouldBe(new HashSet<string> { "id-1", "id-4999" }, ignoreOrder: true);

        (await items.RecordAsync(sub.Id, ["new"], ["id-1"], Ct)).IsSuccess.ShouldBeTrue();
        (await items.KnownAsync(sub.Id, ["id-1", "new"], Ct)).Value!.ShouldBe(new HashSet<string> { "new" });
        (await items.ReplaceAsync("missing", ["x"], Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        (await store.DeleteAsync("dave", sub.Id, Ct)).IsSuccess.ShouldBeTrue();
        await using var count = _source!.CreateCommand("SELECT count(*) FROM nightware_subscription_items");
        ((long)(await count.ExecuteScalarAsync(Ct))!).ShouldBe(0L);
    }

    [Fact]
    public async Task The_console_lists_subscriptions_with_their_polling_state_and_removes_one()
    {
        Assert.SkipUnless(DevStack.Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var schema = new SubscriptionSchema(_source!);
        var store = new PostgresSubscriptionStore(_source!, new CredentialSealer(new string('c', 64)), schema);
        var console = new Nightware.Adapters.Subscriptions.Administration.PostgresChangeSubscriptions(_source!, schema);
        var erin = New("erin") with { ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(1) };
        var frank = New("frank") with { ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(2) };
        await store.CreateAsync(erin, "r1", Ct);
        await store.CreateAsync(frank, "r2", Ct);
        await store.CompletePollAsync(erin.Id, "s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 3, Ct);
        await store.MarkReauthorizationRequestedAsync(erin.Id, DateTimeOffset.UtcNow, Ct);

        var mine = (await console.ListAsync("erin", Ct)).Value.ShouldHaveSingleItem();
        (mine.Subscription.Id, mine.Failures, mine.Health).ShouldBe((erin.Id, 3, Nightware.Application.Administration.Subscriptions.SubscriptionHealth.WaitingForReauthorization));
        (await console.ListAsync(null, Ct)).Value.Select(s => s.Subscription.CreatorId).ShouldContain("frank");

        (await console.RemoveAsync(erin.Id, Ct)).Value.Id.ShouldBe(erin.Id);
        (await console.RemoveAsync(erin.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await store.GetAsync("erin", erin.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

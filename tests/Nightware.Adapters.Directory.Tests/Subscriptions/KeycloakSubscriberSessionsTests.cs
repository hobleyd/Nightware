using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Adapters.Directory.Subscriptions;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Subscriptions;

public sealed class KeycloakSubscriberSessionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100 });
    private readonly IKeycloakTokenClient _tokens = Substitute.For<IKeycloakTokenClient>();
    private readonly BackendCredentialOptions _options = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cache.Dispose();

    private KeycloakSubscriberSessions Sessions() => new(_tokens, _cache, Options.Create(_options), _time);

    private void GivenRefresh(string presented, string access, string next) =>
        _tokens.RefreshAsync(presented, Arg.Any<CancellationToken>()).Returns(new TokenPair(new AccessToken(access, Now.AddMinutes(15)), next));

    [Fact]
    public async Task A_refresh_grant_renews_the_session()
    {
        GivenRefresh("r1", "a1", "r2");

        (await Sessions().RenewAsync("r1", Ct)).Value.ShouldBe(new SubscriberSession("a1", "r2"));
    }

    [Fact]
    public async Task The_next_poll_reuses_the_access_token_until_it_nears_expiry()
    {
        GivenRefresh("r1", "a1", "r2");
        GivenRefresh("r2", "a2", "r3");
        var sessions = Sessions();
        await sessions.RenewAsync("r1", Ct);

        _time.Advance(TimeSpan.FromMinutes(14));
        (await sessions.RenewAsync("r2", Ct)).Value.ShouldBe(new SubscriberSession("a1", "r2"), "cached: the refresh token presented is kept");
        await _tokens.Received(1).RefreshAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(31));
        (await sessions.RenewAsync("r2", Ct)).Value.ShouldBe(new SubscriberSession("a2", "r3"), "within the skew of expiry: refreshed");
    }

    [Fact]
    public async Task A_different_refresh_token_is_not_served_another_sessions_access_token()
    {
        GivenRefresh("r1", "a1", "r2");
        GivenRefresh("other", "b1", "other2");
        var sessions = Sessions();
        await sessions.RenewAsync("r1", Ct);

        (await sessions.RenewAsync("other", Ct)).Value.AccessToken.ShouldBe("b1");
    }

    [Fact]
    public async Task With_the_cache_off_every_poll_refreshes()
    {
        _options.Cache.Enabled = false;
        GivenRefresh("r1", "a1", "r1");
        var sessions = Sessions();

        await sessions.RenewAsync("r1", Ct);
        await sessions.RenewAsync("r1", Ct);

        await _tokens.Received(2).RefreshAsync("r1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_ended_session_passes_through_and_is_not_cached()
    {
        _tokens.RefreshAsync("r1", Arg.Any<CancellationToken>()).Returns(Error.BackendAuthFailed("Session not active"));
        var sessions = Sessions();

        (await sessions.RenewAsync("r1", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        (await sessions.RenewAsync("r1", Ct)).IsSuccess.ShouldBeFalse();
        await _tokens.Received(2).RefreshAsync("r1", Arg.Any<CancellationToken>());
    }
}

using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class AccessTokenCacheTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private int _signIns;

    private static CancellationToken Ct => DnsHostsTestHost.Ct;

    private Task<Result<(string Token, TimeSpan Lifetime)>> SignIn(CancellationToken cancellationToken)
    {
        _signIns++;
        return Task.FromResult(Result<(string, TimeSpan)>.Success(($"token-{_signIns}", TimeSpan.FromMinutes(60))));
    }

    [Fact]
    public async Task One_credential_signs_in_once_until_two_minutes_before_its_token_expires()
    {
        var cache = new AccessTokenCache(_clock);
        DnsProviderToken credential = DnsHostsTestHost.Token(DnsProvider.Hetzner, "token-a");

        (await cache.GetAsync(DnsProvider.Hetzner, credential, SignIn, Ct)).Value.ShouldBe("token-1");
        _clock.Advance(TimeSpan.FromMinutes(57));
        (await cache.GetAsync(DnsProvider.Hetzner, credential, SignIn, Ct)).Value.ShouldBe("token-1");
        _clock.Advance(TimeSpan.FromMinutes(1));
        (await cache.GetAsync(DnsProvider.Hetzner, credential, SignIn, Ct)).Value.ShouldBe("token-2");
    }

    [Fact]
    public async Task Another_credential_or_host_signs_in_for_itself_and_a_failure_is_not_kept()
    {
        var cache = new AccessTokenCache(_clock);

        await cache.GetAsync(DnsProvider.Hetzner, DnsHostsTestHost.Token(DnsProvider.Hetzner, "token-a"), SignIn, Ct);
        await cache.GetAsync(DnsProvider.Hetzner, DnsHostsTestHost.Token(DnsProvider.Hetzner, "token-b"), SignIn, Ct);
        await cache.GetAsync(DnsProvider.Gandi, DnsHostsTestHost.Token(DnsProvider.Gandi, "token-a"), SignIn, Ct);
        _signIns.ShouldBe(3);

        DnsProviderToken refused = DnsHostsTestHost.Token(DnsProvider.Hetzner, "token-c");
        (await cache.GetAsync(DnsProvider.Hetzner, refused, _ => Task.FromResult(Result<(string, TimeSpan)>.Failure(Error.Validation("no", "dnsToken"))), Ct)).IsSuccess.ShouldBeFalse();
        (await cache.GetAsync(DnsProvider.Hetzner, refused, SignIn, Ct)).Value.ShouldBe("token-4");
    }
}

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Subscriptions;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Subscriptions;

public sealed class KeycloakSubscriberCredentialsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_callers_token_is_exchanged_for_a_refresh_token()
    {
        var caller = Substitute.For<IUserContext>();
        caller.BearerToken.Returns("caller-token");
        var tokens = Substitute.For<IKeycloakTokenClient>();
        tokens.ExchangeForRefreshTokenAsync("caller-token", Ct).Returns(new TokenPair(new AccessToken("a", DateTimeOffset.MaxValue), "refresh"));

        (await new KeycloakSubscriberCredentials(caller, tokens).IssueAsync(Ct)).Value.ShouldBe("refresh");
    }

    [Fact]
    public async Task A_refused_exchange_passes_through()
    {
        var caller = Substitute.For<IUserContext>();
        caller.BearerToken.Returns("caller-token");
        var tokens = Substitute.For<IKeycloakTokenClient>();
        tokens.ExchangeForRefreshTokenAsync("caller-token", Ct).Returns(Error.BackendAuthFailed("no"));

        (await new KeycloakSubscriberCredentials(caller, tokens).IssueAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }
}

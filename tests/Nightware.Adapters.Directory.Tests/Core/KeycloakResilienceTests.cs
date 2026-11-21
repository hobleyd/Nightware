using System.Net;

using Nightware.Adapters.Directory.Core;

using Polly;
using Polly.Timeout;

namespace Nightware.Adapters.Directory.Tests.Core;

public sealed class KeycloakResilienceTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)] // a refused grant or exchange
    [InlineData(401, false)]
    [InlineData(429, false)]
    public void Only_server_wide_failures_count_towards_the_circuit(int status, bool counts) =>
        KeycloakResilience.IsServerFailure(Outcome.FromResult(new HttpResponseMessage((HttpStatusCode)status))).ShouldBe(counts);

    [Fact]
    public void Network_errors_and_attempt_timeouts_count_but_cancellation_does_not()
    {
        KeycloakResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new HttpRequestException())).ShouldBeTrue();
        KeycloakResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException())).ShouldBeTrue();
        KeycloakResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new OperationCanceledException())).ShouldBeFalse();
    }
}

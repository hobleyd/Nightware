using System.Net;
using System.Text;

using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Features.Deferred;

/// <summary>ADR 0001 §43: threat submission answers a deliberate 501, not the unknown-route 400 (<c>/shares</c> is Phase 7's now).</summary>
public sealed class DeferredEndpointsTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("POST", "/beta/security/threatSubmission/emailThreats")]
    [InlineData("POST", "/v1.0/security/threatSubmission/emailThreats")]
    public async Task Answers_not_implemented(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = new StringContent("""{"category":"phishing"}""", Encoding.UTF8, "application/json");
        }

        var response = await _host.AuthorizedClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("notImplemented");
    }

    [Fact]
    public async Task Still_require_authentication()
    {
        var response = await _host.Client.PostAsync("/beta/security/threatSubmission/emailThreats", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

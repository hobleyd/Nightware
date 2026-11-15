using System.Net;
using System.Text;

using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Features.Deferred;

/// <summary>ADR 0001 §43: <c>/shares</c> and threat submission answer a deliberate 501, not the unknown-route 400.</summary>
public sealed class DeferredEndpointsTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("GET", "/v1.0/shares/u!aHR0cHM6Ly9maWxlcy5leGFtcGxlLmNvbS9z/driveItem?$select=id,name,size,file,folder")]
    [InlineData("GET", "/v1.0/shares/u!aHR0cHM6Ly9maWxlcy5leGFtcGxlLmNvbS9z/driveItem/content?format=pdf")]
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
        var response = await _host.Client.GetAsync("/v1.0/shares/x/driveItem", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

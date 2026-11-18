using System.Net;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Nightware.Admin.Auth;
using Nightware.Admin.Hosting;

namespace Nightware.Admin.Tests.Hosting;

public sealed class GrafanaStatusTests
{
    [Fact]
    public async Task A_200_from_the_health_url_is_healthy()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        GrafanaHealth health = await Check(handler, new AdminAuthOptions { GrafanaHealthUrl = "http://grafana:3000/grafana/api/health" });

        health.ShouldBe(new GrafanaHealth(true));
        handler.Requested.ShouldBe(new Uri("http://grafana:3000/grafana/api/health"));
    }

    [Theory]
    [InlineData("https://nightware.example/grafana/")]
    [InlineData("https://nightware.example/grafana")]
    public async Task Without_a_health_url_it_asks_grafanas_own(string grafanaUrl)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Check(handler, new AdminAuthOptions { GrafanaUrl = grafanaUrl });

        handler.Requested.ShouldBe(new Uri("https://nightware.example/grafana/api/health"));
    }

    [Fact]
    public async Task An_error_status_is_unhealthy_with_the_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        GrafanaHealth health = await Check(handler, new AdminAuthOptions { GrafanaUrl = "https://nightware.example/grafana/" });

        health.ShouldBe(new GrafanaHealth(false, "Its health check answered 503 Service Unavailable."));
    }

    [Fact]
    public async Task No_answer_is_unhealthy()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused"));

        GrafanaHealth health = await Check(handler, new AdminAuthOptions { GrafanaUrl = "https://nightware.example/grafana/" });

        health.ShouldBe(new GrafanaHealth(false, "It did not answer its health check."));
    }

    [Fact]
    public async Task Without_any_grafana_url_nothing_is_asked()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        (await Check(handler, new AdminAuthOptions())).Healthy.ShouldBeFalse();
        handler.Requested.ShouldBeNull();
    }

    private static async Task<GrafanaHealth> Check(StubHandler handler, AdminAuthOptions options)
    {
        using var http = new HttpClient(handler);
        return await new GrafanaStatus(http, Options.Create(options), NullLogger<GrafanaStatus>.Instance).CheckAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}

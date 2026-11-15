using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>CORS for the NightMail web build: only configured origins, only discovery and Graph routes.</summary>
public sealed class CorsTests
{
    private const string WebOrigin = "https://mail.example.org";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestHost> StartAsync(params string[] origins)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RateLimiting:TokenLimit"] = "3",
            ["RateLimiting:TokensPerPeriod"] = "1",
            ["RateLimiting:ReplenishmentPeriod"] = "00:01:00",
            ["RequestTimeouts:Default"] = "00:00:00.200",
        };
        for (var i = 0; i < origins.Length; i++)
        {
            configuration[$"Cors:AllowedOrigins:{i}"] = origins[i];
        }

        return TestHost.StartAsync(
            static s => s.AddGraphProbes(static g =>
            {
                g.MapGet("/probe", static () => Results.Ok());
                g.MapPatch("/probe", static () => Results.Ok());
                g.MapGet("/boom", static IResult () => throw new InvalidOperationException("boom"));
                g.MapGet("/hang", static async (CancellationToken ct) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return Results.Ok();
                });
            }),
            configuration: configuration);
    }

    private static HttpRequestMessage Preflight(string path, string origin = WebOrigin, string method = "GET")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,prefer,client-request-id");
        return request;
    }

    private static HttpRequestMessage CrossOrigin(string path, string origin = WebOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    [Theory]
    [InlineData("/v1.0/probe", "GET")]
    [InlineData("/v1.0/probe", "PATCH")]
    [InlineData("/beta/probe", "GET")]
    [InlineData("/v1.0/me/not/a/route", "DELETE")]
    [InlineData("/.well-known/nightware", "GET")]
    public async Task A_preflight_from_the_web_origin_is_answered_without_a_token(string path, string method)
    {
        await using var host = await StartAsync(WebOrigin);

        var response = await host.Client.SendAsync(Preflight(path, method: method), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(WebOrigin);
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().ShouldContain(method);
        var allowed = response.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant();
        allowed.ShouldContain("authorization");
        allowed.ShouldContain("client-request-id");
        allowed.ShouldContain("prefer");
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task A_response_exposes_the_headers_the_client_reads()
    {
        await using var host = await StartAsync(WebOrigin);
        var request = CrossOrigin("/v1.0/probe");
        request.Headers.Authorization = new("Bearer", TestHost.Token());

        var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(WebOrigin);
        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers").Single();
        foreach (var header in new[] { "Retry-After", "request-id", "client-request-id", "Location", "ETag" })
        {
            exposed.ShouldContain(header, Case.Insensitive);
        }
    }

    [Theory]
    [InlineData("/v1.0/probe", false, HttpStatusCode.Unauthorized)]
    [InlineData("/v1.0/boom", true, HttpStatusCode.InternalServerError)]
    [InlineData("/v1.0/hang", true, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/v1.0/me/not/a/route", true, HttpStatusCode.BadRequest)]
    public async Task Error_responses_still_carry_cors_headers(string path, bool authorized, HttpStatusCode expected)
    {
        await using var host = await StartAsync(WebOrigin);
        var request = CrossOrigin(path);
        if (authorized)
        {
            request.Headers.Authorization = new("Bearer", TestHost.Token());
        }

        var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(expected);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(WebOrigin);
    }

    [Fact]
    public async Task A_throttled_response_still_carries_cors_headers_and_exposes_retry_after()
    {
        await using var host = await StartAsync(WebOrigin);
        var token = TestHost.Token(sub: "cors-throttled");
        HttpResponseMessage response;
        do
        {
            var request = CrossOrigin("/v1.0/probe");
            request.Headers.Authorization = new("Bearer", token);
            response = await host.Client.SendAsync(request, Ct);
        }
        while (response.StatusCode == HttpStatusCode.OK);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(WebOrigin);
        response.Headers.GetValues("Access-Control-Expose-Headers").Single().ShouldContain("Retry-After", Case.Insensitive);
    }

    [Fact]
    public async Task Other_origins_get_no_cors_headers()
    {
        await using var host = await StartAsync(WebOrigin);

        var preflight = await host.Client.SendAsync(Preflight("/v1.0/probe", origin: "https://evil.example"), Ct);
        var simple = await host.Client.SendAsync(CrossOrigin("/.well-known/nightware", origin: "https://evil.example"), Ct);

        preflight.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        simple.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task By_default_no_origin_is_allowed()
    {
        await using var host = await StartAsync();

        var response = await host.Client.SendAsync(CrossOrigin("/.well-known/nightware"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Health_is_not_exposed_cross_origin()
    {
        await using var host = await StartAsync(WebOrigin);

        var response = await host.Client.SendAsync(CrossOrigin("/healthz"), Ct);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://mail.example.org", true)]
    [InlineData("http://localhost:34572", true)]
    [InlineData("https://mail.example.org/", true)]
    [InlineData("https://mail.example.org/app", false)]
    [InlineData("https://*.example.org", false)]
    [InlineData("*", false)]
    [InlineData("mail.example.org", false)]
    [InlineData("ftp://mail.example.org", false)]
    public void Allowed_origins_must_be_exact_http_origins(string origin, bool valid)
    {
        var settings = new CorsSettings();
        settings.AllowedOrigins.Add(origin);

        new CorsSettingsValidator().Validate(null, settings).Succeeded.ShouldBe(valid);
    }
}

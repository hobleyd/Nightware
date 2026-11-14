using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Api.Graph;
using Nightware.Api.Observability;
using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Api.Tests.Observability;

/// <summary>
/// Drives every kind of request through the real pipeline with <b>every</b> log category at Trace
/// and OpenTelemetry export on, and asserts that no credential, cookie, body or URL secret reaches a
/// log record (message, structured state, scope or exception) or a span attribute.
/// </summary>
public sealed class LogRedactionTests(LogRedactionTests.Scenario scenario) : IClassFixture<LogRedactionTests.Scenario>
{
    private const string CookieSecret = "SENTINEL-COOKIE-6b1d";
    private const string BodySecret = "SENTINEL-BODY-0c7e";
    private const string QuerySecret = "SENTINEL-QUERY-a44f";
    private const string PathSecret = "SENTINEL-PATH-91de";
    private const string UrlTokenSecret = "SENTINEL-URL-TOKEN-4b4b";
    private const string ClientSecret = "SENTINEL-CLIENT-SECRET-77aa";
    private const string ExchangedSecret = "SENTINEL-EXCHANGED-5e2c";
    private const string OutboundSecret = "SENTINEL-OUTBOUND-3f90";
    private const string GarbageToken = "SENTINEL-GARBAGE-TOKEN-d00d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The caller's bearer token is a secret too: it must never appear, nor its signature segment.
    private static readonly string UserToken = TestHost.Token(sub: "redaction-user");
    private static readonly string ExpiredToken = TestHost.Token(expires: DateTime.UtcNow.AddHours(-3));

    public static TheoryData<string> Secrets() =>
    [
        CookieSecret, BodySecret, QuerySecret, UrlTokenSecret, ClientSecret, ExchangedSecret, OutboundSecret, GarbageToken,
        UserToken, Signature(UserToken), ExpiredToken, Signature(ExpiredToken),
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"nightware-api:{ClientSecret}")),
    ];

    [Theory]
    [MemberData(nameof(Secrets))]
    public void No_log_record_contains_the_secret(string secret)
    {
        var leaks = scenario.Logs.Entries.Where(e => e.Text.Contains(secret, StringComparison.Ordinal)).ToList();

        leaks.ShouldBeEmpty($"leaked by: {string.Join(", ", leaks.Select(static l => $"{l.Category} ({l.Level})"))}");
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void No_span_attribute_contains_the_secret(string secret)
    {
        var activities = scenario.Activities;
        var leaks = activities
            .SelectMany(static a => a.TagObjects.Select(t => (a.DisplayName, t.Key, Value: Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)))
            .Where(t => t.Value.Contains(secret, StringComparison.Ordinal))
            .ToList();

        leaks.ShouldBeEmpty($"leaked by: {string.Join(", ", leaks.Select(static l => $"{l.DisplayName}/{l.Key}"))}");
    }

    [Fact]
    public void The_capture_is_not_vacuous()
    {
        var entries = scenario.Logs.Entries;

        // Trace/Debug framework logging really was on, outbound calls were logged, and the failures were.
        entries.ShouldContain(static e => e.Level <= LogLevel.Debug && e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        entries.ShouldContain(static e => e.Category.StartsWith("System.Net.Http.HttpClient.probe-backend", StringComparison.Ordinal));
        entries.ShouldContain(static e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal) && e.Text.Contains("/backend", StringComparison.Ordinal));
        entries.ShouldContain(static e => e.Level == LogLevel.Error && e.Text.Contains("handler failed", StringComparison.Ordinal));
        entries.ShouldContain(static e => e.Category.Contains("JwtBearer", StringComparison.Ordinal));

        scenario.Activities.ShouldContain(static a => a.Kind == ActivityKind.Server);
        scenario.Activities.ShouldContain(static a => a.Kind == ActivityKind.Client);
    }

    [Fact]
    public void Request_paths_are_logged_so_a_url_credential_must_ride_in_the_query()
    {
        // The framework's hosting log scope (RequestPath) and the span's url.path carry the path
        // verbatim; only the query string is redacted. A self-authorising URL therefore puts its
        // credential in the query (as Graph's own ?authtoken= upload URLs do), never in the path.
        scenario.Logs.Entries.ShouldContain(static e => e.Text.Contains(PathSecret, StringComparison.Ordinal));
        scenario.Activities.ShouldContain(static a => Convert.ToString(a.GetTagItem("url.path"), System.Globalization.CultureInfo.InvariantCulture)!.Contains(PathSecret, StringComparison.Ordinal));
    }

    [Fact]
    public void The_request_line_log_stays_off_even_at_trace()
    {
        scenario.Logs.Entries.ShouldNotContain(static e => e.Category == LogRedaction.HostingDiagnosticsCategory && e.Level < LogLevel.Warning);
    }

    [Fact]
    public void Unhandled_exceptions_are_logged_with_the_route_template_not_the_path()
    {
        var entry = scenario.Logs.Entries.Single(static e => e.Level == LogLevel.Error && e.Text.Contains("handler failed", StringComparison.Ordinal));

        entry.Text.ShouldContain("/v1.0/boom");
    }

    private static string Signature(string jwt) => jwt[(jwt.LastIndexOf('.') + 1)..];

    /// <summary>One host and one round of traffic shared by every assertion.</summary>
    public sealed class Scenario : IAsyncLifetime
    {
        private readonly List<Activity> _activities = [];
        private readonly WireMockServer _backend = WireMockServer.Start();
        private ActivityListener _listener = null!;
        private TestHost _host = null!;

        public CapturingLoggerProvider Logs { get; } = new();

        public async ValueTask InitializeAsync()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static _ => true,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_activities)
                    {
                        _activities.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);

            _backend.Given(Request.Create().WithPath("/realms/nightware/protocol/openid-connect/token").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = ExchangedSecret, token_type = "Bearer", expires_in = 300 }));
            _backend.Given(Request.Create().WithPath("/backend").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(500).WithBody("backend failure"));

            var configuration = new Dictionary<string, string?>
            {
                // An operator turning everything up — including the request-line log the façade pins off.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
                ["Logging:LogLevel:System"] = "Trace",
                ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:9",
                ["Directory:KeycloakBaseUrl"] = _backend.Url,
                ["Directory:ClientSecret"] = ClientSecret,
                ["BackendCredentials:Mail:Strategy"] = "TokenExchange",
                ["BackendCredentials:Mail:Audience"] = "stalwart",
            };

            _host = await TestHost.StartAsync(
                services =>
                {
                    services.AddSingleton<ILoggerProvider>(Logs);
                    services.AddHttpClient("probe-backend");
                    services.AddGraphProbes(g =>
                    {
                        g.MapGet("/probe", static () => Results.Ok(new { ok = true }));
                        g.MapPost("/echo", static async (HttpRequest request) =>
                        {
                            using var reader = new StreamReader(request.Body);
                            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
                            return Results.Ok(new { length = body.Length });
                        });
                        g.MapPost("/bad-json", static (Nightware.Api.Features.Mail.Dto.MailFolderDto folder) => Results.Ok(folder.DisplayName));
                        g.MapGet("/boom", static IResult () => throw new InvalidOperationException("handler failed"));
                        g.MapGet("/uploads/{token}", static (string token) => Results.Ok()).AllowAnonymous();
                        g.MapGet("/exchange", static async (IBackendCredentials credentials, CancellationToken ct) =>
                            GraphResults.Match(await credentials.ForAsync(Backend.Mail, ct), static _ => Results.Ok()));
                        g.MapGet("/outbound", async (IHttpClientFactory factory, CancellationToken ct) =>
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_backend.Url}/backend?sig={OutboundSecret}");
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", OutboundSecret);
                            request.Headers.Add("Cookie", $"backend={OutboundSecret}");
                            using var response = await factory.CreateClient("probe-backend").SendAsync(request, ct);
                            return Results.Ok(new { status = (int)response.StatusCode });
                        });
                    });
                },
                configuration: configuration);

            await DriveTrafficAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            _listener.Dispose();
            _backend.Dispose();
        }

        private async Task DriveTrafficAsync()
        {
            var client = _host.AuthorizedClient(UserToken);
            client.DefaultRequestHeaders.Add("Cookie", $"session={CookieSecret}");

            (await client.GetAsync($"/v1.0/probe?$search=%22{QuerySecret}%22&x={QuerySecret}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.PostAsync("/v1.0/echo", new StringContent($"{{\"body\":{{\"content\":\"{BodySecret}\"}}}}", Encoding.UTF8, "application/json"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.PostAsync("/v1.0/bad-json", new StringContent($"{{\"displayName\": {BodySecret}", Encoding.UTF8, "application/json"), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await client.GetAsync("/v1.0/boom", Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            (await client.GetAsync($"/v1.0/uploads/{PathSecret}?authtoken={UrlTokenSecret}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/v1.0/exchange", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync("/v1.0/outbound", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync($"/v1.0/no/such/{PathSecret}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            (await _host.AuthorizedClient(GarbageToken).GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await _host.AuthorizedClient(ExpiredToken).GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await _host.AuthorizedClient(UserToken[..^4] + "AAAA").GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        public IReadOnlyList<Activity> Activities
        {
            get
            {
                lock (_activities)
                {
                    return [.. _activities];
                }
            }
        }
    }
}

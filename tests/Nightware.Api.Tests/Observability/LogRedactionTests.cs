using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Api.Graph;
using Nightware.Hosting.Observability;
using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

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

    // A Nextcloud public-share token is a bearer secret: anyone holding it reads the file. It reaches the
    // façade base64url-encoded inside a Graph share id, and leaves it in the path of public.php/dav/files/{token}.
    private const string ShareToken = "SENTINELSHARE7c1e9";
    private const string ShareSite = "https://cloud.redaction.test";
    private static readonly string ShareId = "u!" + System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{ShareSite}/s/{ShareToken}"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The caller's bearer token is a secret too: it must never appear, nor its signature segment.
    private static readonly string UserToken = TestHost.Token(sub: "redaction-user");
    private static readonly string ExpiredToken = TestHost.Token(expires: DateTime.UtcNow.AddHours(-3));

    public static TheoryData<string> Secrets() =>
    [
        CookieSecret, BodySecret, QuerySecret, UrlTokenSecret, ClientSecret, ExchangedSecret, OutboundSecret, GarbageToken,
        UserToken, Signature(UserToken), ExpiredToken, Signature(ExpiredToken),
        ShareToken, ShareId[2..],
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
    public void No_log_record_or_span_contains_a_minted_url_credential()
    {
        // The sealed authtokens of the download URL /content redirected to and of the upload URL
        // createUploadSession returned: each carries the caller's access token.
        scenario.MintedSecrets.Count.ShouldBe(2);
        foreach (var secret in scenario.MintedSecrets)
        {
            No_log_record_contains_the_secret(secret);
            No_span_attribute_contains_the_secret(secret);
            secret.Length.ShouldBeGreaterThan(40);
        }
    }

    [Fact]
    public void Share_ids_and_public_share_tokens_are_redacted_where_paths_are_kept()
    {
        var entries = scenario.Logs.Entries;

        // Inbound: the hosting scope's RequestPath on every record of the request.
        entries.ShouldContain(static e => e.Scopes.Any(static s => s.Any(static kv =>
            kv.Key == "RequestPath" && Equals(kv.Value, "/v1.0/shares/{redacted}/driveItem"))));
        // Outbound: HttpClient's own request logs for Nextcloud's public-share WebDAV.
        entries.ShouldContain(static e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
            && e.Text.Contains("/public.php/dav/files/{redacted}", StringComparison.Ordinal));
        // Spans: the server span's url.path and the client span's url.full.
        scenario.Activities.ShouldContain(static a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("url.path"), "/v1.0/shares/{redacted}/driveItem/content"));
        scenario.Activities.ShouldContain(static a => a.Kind == ActivityKind.Client
            && Convert.ToString(a.GetTagItem("url.full"), System.Globalization.CultureInfo.InvariantCulture)!.Contains("/public.php/dav/files/{redacted}", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_records_still_carry_the_trace_context()
    {
        scenario.Logs.Entries.ShouldContain(static e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            && e.Scopes.Any(static s => s.Any(static kv => kv.Key == "TraceId")) && e.Scopes.Any(static s => s.Any(static kv => kv.Key == "SpanId")));
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
        private readonly ITaskAttachmentStore _attachments = Substitute.For<ITaskAttachmentStore>();
        private readonly List<string> _minted = [];
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
            _backend.Given(Request.Create().WithPath($"/public.php/dav/files/{ShareToken}").UsingMethod("PROPFIND"))
                .RespondWith(Response.Create().WithStatusCode(207).WithHeader("Content-Type", "application/xml").WithBody(
                    $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns"><d:response><d:href>/public.php/dav/files/{ShareToken}</d:href><d:propstat><d:prop><d:displayname>probe.txt</d:displayname><d:getcontenttype>text/plain</d:getcontenttype><d:getcontentlength>5</d:getcontentlength><d:resourcetype/><oc:fileid>385</oc:fileid></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>"""));
            _backend.Given(Request.Create().WithPath($"/public.php/dav/files/{ShareToken}").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/plain").WithBody("hello"));

            var session = new UploadSession("store-session-token", DateTimeOffset.UtcNow.AddMinutes(15), 4096);
            _attachments.BeginUploadAsync(Arg.Any<MailboxRef>(), Arg.Any<TaskListId>(), Arg.Any<TaskId>(), "big.eml", "message/rfc822", 4096, Arg.Any<CancellationToken>())
                .Returns(session);
            _attachments.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == session.Token), 0, 4095, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new UploadProgress([], new TaskAttachment(new AttachmentId("att-1"), "big.eml", "message/rfc822", 4096)));

            var configuration = new Dictionary<string, string?>
            {
                // An operator turning everything up — including the request-line log the façade pins off.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
                ["Logging:LogLevel:System"] = "Trace",
                ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Trace",
                ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:9",
                ["Directory:KeycloakBaseUrl"] = _backend.Url,
                ["Directory:ClientSecret"] = ClientSecret,
                ["BackendCredentials:Mail:Strategy"] = "TokenExchange",
                ["BackendCredentials:Mail:Audience"] = "stalwart",
                ["Dav:BaseUrl"] = _backend.Url + "/",
                ["Discovery:FilesBaseUrls:0"] = ShareSite,
            };

            _host = await TestHost.StartAsync(
                services =>
                {
                    services.AddSingleton<ILoggerProvider>(Logs);
                    services.AddSingleton(_attachments);
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

            // Documents: the share id in the path, the sealed download URL, Nextcloud's public-share WebDAV.
            using var anonymous = _host.App.GetTestClient();
            (await client.GetAsync($"/v1.0/shares/{ShareId}/driveItem", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await client.GetAsync($"/v1.0/shares/{ShareId}/bogus", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using var redirect = await client.GetAsync($"/v1.0/shares/{ShareId}/driveItem/content", Ct);
            redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            var download = redirect.Headers.Location!;
            _minted.Add(System.Web.HttpUtility.ParseQueryString(download.Query)["authtoken"]!);
            using var downloaded = await anonymous.GetAsync(download.PathAndQuery, Ct);
            downloaded.StatusCode.ShouldBe(HttpStatusCode.OK);

            // To Do: the sealed upload URL, PUT without a bearer.
            using var created = await client.PostAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments/createUploadSession", new StringContent(
                """{"attachmentItem":{"attachmentType":"file","contentType":"message/rfc822","name":"big.eml","size":4096}}""", Encoding.UTF8, "application/json"), Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            var upload = new Uri(System.Text.Json.Nodes.JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!["uploadUrl"]!.GetValue<string>());
            _minted.Add(System.Web.HttpUtility.ParseQueryString(upload.Query)["authtoken"]!);
            using var put = new ByteArrayContent(new byte[4096]);
            put.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-4095/4096");
            (await anonymous.PutAsync(upload.PathAndQuery, put, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

            (await _host.AuthorizedClient(GarbageToken).GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await _host.AuthorizedClient(ExpiredToken).GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await _host.AuthorizedClient(UserToken[..^4] + "AAAA").GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        public IReadOnlyList<string> MintedSecrets => _minted;

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

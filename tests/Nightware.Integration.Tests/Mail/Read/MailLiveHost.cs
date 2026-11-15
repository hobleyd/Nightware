using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Nightware.Integration.Tests.Mail.Read;

/// <summary>
/// An in-process Nightware against the running dev stack, as NightMail sees it (real Keycloak tokens,
/// token exchange to Stalwart), plus a direct JMAP channel to seed and change alice's mail the way
/// another client would. Opt-in: skips unless <c>NIGHTWARE_INTEGRATION=1</c>.
/// </summary>
/// <remarks>
/// The Caddy CA is trusted for Nightware's own HTTP clients only, never process-wide.
/// </remarks>
internal sealed class MailLiveHost : IAsyncDisposable
{
    public const string Domain = "nightware.localhost";
    public const string GraphBase = "https://graph." + Domain + "/v1.0";
    public const string Alice = "alice@" + Domain;

    private const string Issuer = "https://auth." + Domain + "/realms/nightware";
    private const string MailBase = "https://mail." + Domain;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly X509Certificate2 _ca;
    private readonly Dictionary<string, string> _env;
    private readonly HttpClient _backchannel;
    private string? _jmapToken;

    private MailLiveHost(string deployDir)
    {
        _env = ReadEnv(Path.Combine(deployDir, ".env"));
        _ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(deployDir, ".certs", "caddy-root.crt"));
        _backchannel = new HttpClient(Trusting(new SocketsHttpHandler()));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = Issuer,
                ["Discovery:GraphBaseUrl"] = GraphBase,
                ["Discovery:GraphBetaBaseUrl"] = "https://graph." + Domain + "/beta",
                ["DeltaLinks:SigningKey"] = "integration",
                ["Directory:KeycloakBaseUrl"] = "https://auth." + Domain,
                ["Directory:ClientSecret"] = _env["NIGHTWARE_API_CLIENT_SECRET"],
                ["Jmap:SessionUrl"] = MailBase + "/.well-known/jmap",
            }));
            builder.ConfigureTestServices(services =>
            {
                // Registered after the app's own client configuration, so it runs after every
                // ConfigurePrimaryHttpMessageHandler (the JMAP and Keycloak clients set their own).
                services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => Trusting(b.PrimaryHandler)));

                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = Trusting(new SocketsHttpHandler()));
            });
        });
    }

    public static MailLiveHost Start()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NIGHTWARE_INTEGRATION") == "1", "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var deployDir = Environment.GetEnvironmentVariable("NIGHTWARE_DEPLOY_DIR") ?? FindDeployDir()
            ?? throw new InvalidOperationException("No bootstrapped deploy folder (deploy/.env) found; set NIGHTWARE_DEPLOY_DIR.");
        return new MailLiveHost(deployDir);
    }

    /// <summary>The in-process Graph façade, authenticated as alice with a NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphAsync(CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(cancellationToken));
        return client;
    }

    /// <summary>The path and query of a link the façade emitted on its public base.</summary>
    public static string Local(string link)
    {
        link.ShouldStartWith(GraphBase + "/", Case.Sensitive, "links must sit on the configured graphBaseUrl");
        return "/v1.0" + link[GraphBase.Length..];
    }

    /// <summary>Alice's JMAP account id and her mailboxes by role.</summary>
    public async Task<(string Account, IReadOnlyDictionary<string, string> MailboxByRole)> MailboxesAsync(CancellationToken cancellationToken)
    {
        var session = await GetJsonAsync(MailBase + "/jmap/session", cancellationToken);
        var account = session["primaryAccounts"]!["urn:ietf:params:jmap:mail"]!.GetValue<string>();
        var response = await JmapAsync([new JsonArray("Mailbox/get", new JsonObject { ["accountId"] = account, ["properties"] = new JsonArray("id", "role") }, "m")], cancellationToken);
        var roles = response[0]![1]!["list"]!.AsArray()
            .Where(static m => m!["role"] is not null)
            .ToDictionary(static m => m!["role"]!.GetValue<string>(), static m => m!["id"]!.GetValue<string>());
        return (account, roles);
    }

    /// <summary>Imports raw MIME into a mailbox (as delivery would) and returns the JMAP email id.</summary>
    public async Task<string> ImportAsync(string account, string mailboxId, string mime, CancellationToken cancellationToken)
    {
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"{MailBase}/jmap/upload/{account}/") { Content = new StringContent(mime, Encoding.UTF8) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var uploaded = await _backchannel.SendAsync(upload, cancellationToken);
        uploaded.EnsureSuccessStatusCode();
        var blobId = (await uploaded.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["blobId"]!.GetValue<string>();

        var import = new JsonObject
        {
            ["accountId"] = account,
            ["emails"] = new JsonObject
            {
                ["i"] = new JsonObject { ["blobId"] = blobId, ["mailboxIds"] = new JsonObject { [mailboxId] = true }, ["keywords"] = new JsonObject() },
            },
        };
        var response = await JmapAsync([new JsonArray("Email/import", import, "i")], cancellationToken);
        return response[0]![1]!["created"]!["i"]!["id"]!.GetValue<string>();
    }

    /// <summary>Applies a JMAP <c>Email/set</c> update (e.g. a move or a keyword) or destroys an email.</summary>
    public async Task SetAsync(string account, string emailId, JsonObject? patch, CancellationToken cancellationToken)
    {
        var set = new JsonObject { ["accountId"] = account };
        if (patch is null)
        {
            set["destroy"] = new JsonArray(emailId);
        }
        else
        {
            set["update"] = new JsonObject { [emailId] = patch };
        }

        var response = await JmapAsync([new JsonArray("Email/set", set, "s")], cancellationToken);
        var result = response[0]![1]!.AsObject();
        (result["notUpdated"] ?? result["notDestroyed"]).ShouldBeNull(result.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _ca.Dispose();
    }

    private async Task<JsonArray> JmapAsync(JsonArray calls, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail"),
            ["methodCalls"] = calls,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, MailBase + "/jmap/") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["methodResponses"]!.AsArray();
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
    }

    private Task<string> UserTokenAsync(CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _env["NIGHTWARE_DEV_CLIENT_SECRET"]),
                new("username", "alice"),
                new("password", _env["TEST_USER_PASSWORD"]),
                new("scope", "openid email profile"),
            ],
            null,
            cancellationToken);

    private async Task<string> JmapTokenAsync(CancellationToken cancellationToken) =>
        _jmapToken ??= await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", await UserTokenAsync(cancellationToken)),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", "stalwart"),
            ],
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("nightware-api:" + _env["NIGHTWARE_API_CLIENT_SECRET"]))),
            cancellationToken);

    private async Task<string> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, AuthenticationHeaderValue? auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = auth;
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
    }

    private HttpMessageHandler Trusting(HttpMessageHandler handler)
    {
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
        }

        return handler;
    }

    private bool IsTrusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }

    private static string? FindDeployDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "deploy");
            if (File.Exists(Path.Combine(candidate, ".env")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadEnv(string path) =>
        File.ReadAllLines(path)
            .Select(static l => l.Trim())
            .Where(static l => l.Length > 0 && !l.StartsWith('#') && l.Contains('=', StringComparison.Ordinal))
            .Select(static l => (Key: l[..l.IndexOf('=', StringComparison.Ordinal)], Value: l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim('"')))
            .GroupBy(static kv => kv.Key)
            .ToDictionary(static g => g.Key, static g => g.Last().Value);
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Integration.Tests.Directory;

/// <summary>
/// The running dev stack (<c>deploy/</c>) and an in-process Nightware pointed at it. Opt-in: tests skip
/// unless <c>NIGHTWARE_INTEGRATION=1</c>.
/// </summary>
/// <remarks>
/// <para>The stack's secrets and Caddy root CA live in the bootstrapped deploy folder
/// (<c>deploy/.env</c>, <c>deploy/.certs/caddy-root.crt</c>), which is git-ignored. Set
/// <c>NIGHTWARE_DEPLOY_DIR</c> when that is not this checkout's own <c>deploy/</c>.</para>
/// <para>Every backend is reached on its public <c>https://*.nightware.localhost</c> name through Caddy; the
/// Caddy CA is trusted for Nightware's outgoing HTTP clients and JWKS fetch only, never process-wide.</para>
/// </remarks>
internal sealed class LiveStack : IAsyncDisposable
{
    public const string Domain = "nightware.localhost";
    public const string GraphBase = "https://graph." + Domain + "/v1.0";
    public const string CloudBase = "https://cloud." + Domain;
    public const string Issuer = "https://auth." + Domain + "/realms/nightware";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly X509Certificate2 _ca;
    private readonly Dictionary<string, string> _env;

    private LiveStack(string deployDir)
    {
        _env = ReadEnv(Path.Combine(deployDir, ".env"));
        _ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(deployDir, ".certs", "caddy-root.crt"));
        Backchannel = new HttpClient(TrustingHandler(_ca));
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
                ["Dav:BaseUrl"] = CloudBase + "/",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler((handler, _) => Trust(handler, _ca)));
                // Configure, not PostConfigure: the framework's own post-configure builds the metadata manager
                // from BackchannelHttpHandler.
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = TrustingHandler(_ca));
            });
        });
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("NIGHTWARE_INTEGRATION") == "1";

    /// <summary>An HTTP client trusting the stack's CA, for setting up data directly on the backends.</summary>
    public HttpClient Backchannel { get; }

    public static LiveStack Start()
    {
        Assert.SkipUnless(Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var deployDir = Environment.GetEnvironmentVariable("NIGHTWARE_DEPLOY_DIR") ?? FindDeployDir()
            ?? throw new InvalidOperationException("No bootstrapped deploy folder (deploy/.env) found; set NIGHTWARE_DEPLOY_DIR.");
        return new LiveStack(deployDir);
    }

    /// <summary>A client of the in-process Nightware carrying <paramref name="user"/>'s NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphClientAsync(string user, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    /// <summary>A password-grant token from the dev-only <c>nightware-dev</c> client (<c>aud nightware-api</c>).</summary>
    public Task<string> UserTokenAsync(string user, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _env["NIGHTWARE_DEV_CLIENT_SECRET"]),
                new("username", user),
                new("password", _env["TEST_USER_PASSWORD"]),
                new("scope", "openid email profile"),
            ],
            null,
            cancellationToken);

    /// <summary><paramref name="user"/>'s token exchanged for audience <c>nextcloud</c>, as the façade would send it.</summary>
    public async Task<string> NextcloudTokenAsync(string user, CancellationToken cancellationToken)
    {
        var subject = await UserTokenAsync(user, cancellationToken);
        return await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", subject),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", "nextcloud"),
            ],
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("nightware-api:" + _env["NIGHTWARE_API_CLIENT_SECRET"]))),
            cancellationToken);
    }

    /// <summary>The path and query of a link Nightware emitted on its public base, for the in-process client.</summary>
    public static string Local(string link)
    {
        link.ShouldStartWith(GraphBase + "/", Case.Sensitive, "links must sit on the configured graphBaseUrl");
        return "/v1.0" + link[GraphBase.Length..];
    }

    public async ValueTask DisposeAsync()
    {
        Backchannel.Dispose();
        await _factory.DisposeAsync();
        _ca.Dispose();
    }

    private async Task<string> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, AuthenticationHeaderValue? auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = auth;
        using var response = await Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("access_token").GetString()!;
    }

    private static void Trust(HttpMessageHandler handler, X509Certificate2 ca)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors, ca);
                break;
            case HttpClientHandler classic:
                classic.ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors, ca);
                break;
        }
    }

    private static SocketsHttpHandler TrustingHandler(X509Certificate2 ca)
    {
        var handler = new SocketsHttpHandler();
        Trust(handler, ca);
        return handler;
    }

    private static bool IsTrusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2 ca)
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
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Caddy serves leaf + intermediate; the intermediate must be available to build up to its root.
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

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
using Nightware.Application.Mail;

namespace Nightware.Integration.Tests.Calendar;

/// <summary>
/// The running dev stack and an in-process Nightware pointed at it, like 5A's <c>Directory/LiveStack</c>, plus
/// one test-only service: <see cref="JmapInvitationReader"/> stands in for Phase 2's mail reader so the
/// invitation-mail → event flow can run before Phase 2 merges. Opt-in with <c>NIGHTWARE_INTEGRATION=1</c>;
/// <c>NIGHTWARE_DEPLOY_DIR</c> names a bootstrapped deploy folder (<c>.env</c> or <c>.env.example</c>, and <c>.certs/</c>).
/// </summary>
internal sealed class CalendarLiveStack : IAsyncDisposable
{
    public const string Domain = "nightware.localhost";
    public const string Issuer = "https://auth." + Domain + "/realms/nightware";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly X509Certificate2 _ca;
    private readonly Dictionary<string, string> _env;
    private readonly HttpClient _backchannel;

    private CalendarLiveStack(string deployDir)
    {
        var envFile = Path.Combine(deployDir, ".env");
        _env = ReadEnv(File.Exists(envFile) ? envFile : Path.Combine(deployDir, ".env.example"));
        _ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(deployDir, ".certs", "caddy-root.crt"));
        _backchannel = new HttpClient(TrustingHandler(_ca));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = Issuer,
                ["DeltaLinks:SigningKey"] = "integration",
                ["Directory:ClientSecret"] = _env["NIGHTWARE_API_CLIENT_SECRET"],
                ["Jmap:SessionUrl"] = JmapInvitationReader.SessionUrl,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler((handler, _) => Trust(handler, _ca)));
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = TrustingHandler(_ca));
                services.AddScoped<IMailMessageReader, JmapInvitationReader>();
            });
        });
    }

    public static CalendarLiveStack Start()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("NIGHTWARE_INTEGRATION") == "1", "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var deployDir = Environment.GetEnvironmentVariable("NIGHTWARE_DEPLOY_DIR") ?? FindDeployDir()
            ?? throw new InvalidOperationException("No bootstrapped deploy folder found; set NIGHTWARE_DEPLOY_DIR.");
        return new CalendarLiveStack(deployDir);
    }

    /// <summary>A client of the in-process Nightware carrying <paramref name="user"/>'s NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphClientAsync(string user, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _ca.Dispose();
    }

    private async Task<string> UserTokenAsync(string user, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _env["NIGHTWARE_DEV_CLIENT_SECRET"]),
                new("username", user),
                new("password", _env["TEST_USER_PASSWORD"]),
                new("scope", "openid email profile"),
            ]),
        };
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
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
        foreach (var element in presented?.ChainElements ?? [])
        {
            chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        }

        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }

    private static string? FindDeployDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "deploy");
            if (File.Exists(Path.Combine(candidate, ".certs", "caddy-root.crt")))
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

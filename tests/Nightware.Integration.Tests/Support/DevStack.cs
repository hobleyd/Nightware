using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Nightware.Integration.Tests.Support;

/// <summary>
/// The bootstrapped dev stack's deploy folder: its <c>.env</c> (else <c>.env.example</c>) secrets and the Caddy root CA, trusted for
/// the clients the tests and the in-process Nightware use, never process-wide. Every live host builds on this.
/// </summary>
/// <remarks>
/// The folder is git-ignored and lives where the stack was bootstrapped; set <c>NIGHTWARE_DEPLOY_DIR</c>
/// when that is not this checkout's own <c>deploy/</c>.
/// </remarks>
internal sealed class DevStack : IDisposable
{
    public const string Domain = "nightware.localhost";
    public const string GraphBase = "https://graph." + Domain + "/v1.0";
    public const string GraphBetaBase = "https://graph." + Domain + "/beta";
    public const string Issuer = "https://auth." + Domain + "/realms/nightware";

    private readonly Dictionary<string, string> _env;
    private readonly X509Certificate2 _ca;

    private DevStack(string deployDir)
    {
        // A stack bootstrapped with the defaults may have no .env of its own; its values are .env.example's.
        var envFile = Path.Combine(deployDir, ".env");
        _env = ReadEnv(File.Exists(envFile) ? envFile : Path.Combine(deployDir, ".env.example"));
        _ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(deployDir, ".certs", "caddy-root.crt"));
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("NIGHTWARE_INTEGRATION") == "1";

    /// <summary>The stack, or a skipped test when live tests are not enabled.</summary>
    public static DevStack Open()
    {
        Assert.SkipUnless(Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var deployDir = Environment.GetEnvironmentVariable("NIGHTWARE_DEPLOY_DIR") ?? FindDeployDir()
            ?? throw new InvalidOperationException("No bootstrapped deploy folder (deploy/.certs/caddy-root.crt) found; set NIGHTWARE_DEPLOY_DIR.");
        return new DevStack(deployDir);
    }

    /// <summary>A value from <c>deploy/.env</c>.</summary>
    public string Env(string key) => _env[key];

    /// <summary>An HTTP client trusting the stack's CA, for tokens and for checking the backends directly.</summary>
    public HttpClient Backchannel() => new(Trusting(new SocketsHttpHandler()));

    /// <summary>
    /// Makes the in-process Nightware trust the stack's CA: on every named client's real primary handler,
    /// and on the JWT bearer's metadata channel.
    /// </summary>
    public void Trust(IServiceCollection services)
    {
        // Each adapter client sets its own (cookie-free, no-redirect) primary handler, which replaces anything
        // ConfigureHttpClientDefaults supplies; a handler-builder action runs afterwards, on the real one.
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => Trusting(b.PrimaryHandler)));

        // Configure, not PostConfigure: the framework's own post-configure builds the metadata manager from it.
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = Trusting(new SocketsHttpHandler()));
    }

    public HttpMessageHandler Trusting(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
                break;
            case HttpClientHandler classic:
                classic.ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
                break;
        }

        return handler;
    }

    public void Dispose() => _ca.Dispose();

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

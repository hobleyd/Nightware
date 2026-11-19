using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Nightware.Integration.Tests.Support;

/// <summary>
/// The bootstrapped dev stack's deploy folder: its public URL and mail domain, its <c>.env</c> secrets and, with
/// <c>CADDY_TLS=internal</c>, the Caddy root CA, trusted for the clients the tests and the in-process Nightware
/// use, never process-wide. Every live host builds on this.
/// </summary>
/// <remarks>
/// <para>The folder is git-ignored and lives where the stack was bootstrapped; set <c>NIGHTWARE_DEPLOY_DIR</c>
/// when that is not this checkout's own <c>deploy/</c>.</para>
/// <para>URLs follow the stack's single-host layout (<c>deploy/caddy/Caddyfile</c>):
/// <c>https://{PUBLIC_HOST}{PUBLIC_PORT_SUFFIX}</c> with <c>/auth</c>, <c>/cloud</c> and <c>/graph</c>, and
/// Stalwart's JMAP at the root. Without a <c>.env</c> (live tests disabled) they are the defaults, so the static
/// members never throw.</para>
/// <para>A <c>PUBLIC_HOST</c> that DNS does not resolve (yet) is reached by setting
/// <c>NIGHTWARE_CONNECT_ADDRESS</c> (e.g. <c>127.0.0.1</c>): connections to it go there instead, with TLS and
/// SNI still on the host name. <c>deploy/scripts/lib.sh</c> reads the same variable.</para>
/// </remarks>
internal sealed class DevStack : IDisposable
{
    private const string DefaultHost = "nightware.localhost";

    private static readonly Lazy<(string? Dir, Dictionary<string, string> Env)> Located = new(Locate);

    private readonly Dictionary<string, string> _env;
    private readonly X509Certificate2? _ca;

    private DevStack(string deployDir)
    {
        _env = Located.Value.Env;
        if (InternalTls)
        {
            var caFile = Path.Combine(deployDir, ".certs", "caddy-root.crt");
            if (!File.Exists(caFile))
            {
                throw new InvalidOperationException($"CADDY_TLS=internal but {caFile} is missing; run deploy/bootstrap.sh.");
            }

            _ca = X509CertificateLoader.LoadCertificateFromFile(caFile);
        }
    }

    public static bool Enabled => Environment.GetEnvironmentVariable("NIGHTWARE_INTEGRATION") == "1";

    /// <summary>The one public host name (<c>PUBLIC_HOST</c>).</summary>
    public static string PublicHost => Setting("PUBLIC_HOST") ?? Setting("BASE_DOMAIN") ?? DefaultHost;

    /// <summary>The test users' and rooms' mail domain (<c>MAIL_DOMAIN</c>).</summary>
    public static string MailDomain => Setting("MAIL_DOMAIN") ?? Setting("BASE_DOMAIN") ?? DefaultHost;

    /// <summary><c>https://{PUBLIC_HOST}{PUBLIC_PORT_SUFFIX}</c>, without a trailing slash.</summary>
    public static string PublicUrl => $"https://{PublicHost}{Setting("PUBLIC_PORT_SUFFIX")}";

    public static string AuthBase => PublicUrl + "/auth";

    public static string Issuer => AuthBase + "/realms/nightware";

    public static string CloudBase => PublicUrl + "/cloud";

    /// <summary>Stalwart's JMAP (<c>/.well-known/jmap</c>, <c>/jmap/</c>) is at the host root.</summary>
    public static string MailBase => PublicUrl;

    /// <summary>Everything else Stalwart serves (e.g. <c>/healthz</c>) is under this stripped alias.</summary>
    public static string MailAliasBase => PublicUrl + "/mail";

    public static string GraphBase => PublicUrl + "/graph/v1.0";

    public static string GraphBetaBase => PublicUrl + "/graph/beta";

    /// <summary><c>{user}@{MAIL_DOMAIN}</c>.</summary>
    public static string Address(string user) => $"{user}@{MailDomain}";

    private static bool InternalTls => (Setting("CADDY_TLS") ?? "internal") == "internal";

    private static string? ConnectAddress => Environment.GetEnvironmentVariable("NIGHTWARE_CONNECT_ADDRESS") is { Length: > 0 } a ? a : null;

    /// <summary>The stack, or a skipped test when live tests are not enabled.</summary>
    public static DevStack Open()
    {
        Assert.SkipUnless(Enabled, "Live integration tests run only with NIGHTWARE_INTEGRATION=1.");
        var deployDir = Located.Value.Dir
            ?? throw new InvalidOperationException("No bootstrapped deploy folder (deploy/.env) found; set NIGHTWARE_DEPLOY_DIR.");
        return new DevStack(deployDir);
    }

    /// <summary>A value from <c>deploy/.env</c>.</summary>
    public string Env(string key) => _env[key];

    /// <summary>Whether <c>deploy/.env</c> sets <paramref name="key"/>.</summary>
    public bool Has(string key) => _env.ContainsKey(key);

    /// <summary>
    /// The in-process façade's settings for this stack, every backend on its public URL (as a façade outside the
    /// Docker network sees them); a host adds or overrides its own.
    /// </summary>
    public Dictionary<string, string?> FacadeSettings() => new()
    {
        ["Auth:Authority"] = Issuer,
        ["Discovery:GraphBaseUrl"] = GraphBase,
        ["Discovery:GraphBetaBaseUrl"] = GraphBetaBase,
        ["Discovery:AuthorizationEndpoint"] = Issuer + "/protocol/openid-connect/auth",
        ["Discovery:TokenEndpoint"] = Issuer + "/protocol/openid-connect/token",
        ["DeltaLinks:SigningKey"] = "integration",
        ["Directory:KeycloakBaseUrl"] = AuthBase,
        ["Directory:ClientSecret"] = Env("NIGHTWARE_API_CLIENT_SECRET"),
        ["Jmap:SessionUrl"] = MailBase + "/.well-known/jmap",
        ["Dav:BaseUrl"] = CloudBase + "/",
    };

    /// <summary>An HTTP client trusting the stack's CA, for tokens and for checking the backends directly.</summary>
    public HttpClient Backchannel() => new(Trusting(new SocketsHttpHandler()));

    /// <summary>
    /// Makes the in-process Nightware trust the stack's CA and reach <see cref="PublicHost"/>: on every named
    /// client's real primary handler, and on the JWT bearer's metadata channel.
    /// </summary>
    public void Trust(IServiceCollection services)
    {
        // Each adapter client sets its own (cookie-free, no-redirect) primary handler, which replaces anything
        // ConfigureHttpClientDefaults supplies; a handler-builder action runs afterwards, on the real one.
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => Trusting(b.PrimaryHandler)));

        // Configure, not PostConfigure: the framework's own post-configure builds the metadata manager from it.
        // The authority too: AddGraphAuthentication reads Auth:Authority while Program registers services, before
        // the factory's in-memory settings exist, so it would otherwise keep appsettings.json's default issuer.
        services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            o.Authority = Issuer;
            o.BackchannelHttpHandler = Trusting(new SocketsHttpHandler());
        });
    }

    public HttpMessageHandler Trusting(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
                if (ConnectAddress is { } address)
                {
                    sockets.ConnectCallback = (context, cancellationToken) => ConnectAsync(context, address, cancellationToken);
                }

                break;
            case HttpClientHandler classic:
                // No connect hook here: every primary handler in Nightware is a SocketsHttpHandler.
                classic.ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
                break;
        }

        return handler;
    }

    public void Dispose() => _ca?.Dispose();

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, string address, CancellationToken cancellationToken)
    {
        var target = string.Equals(context.DnsEndPoint.Host, PublicHost, StringComparison.OrdinalIgnoreCase) ? address : context.DnsEndPoint.Host;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool IsTrusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // A publicly trusted certificate needs no help: the platform's verdict stands.
        if (_ca is null || certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
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

    private static string? Setting(string key) =>
        Located.Value.Env.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static (string? Dir, Dictionary<string, string> Env) Locate()
    {
        var dir = Environment.GetEnvironmentVariable("NIGHTWARE_DEPLOY_DIR") ?? FindDeployDir();
        var envFile = dir is null ? null : Path.Combine(dir, ".env");
        return envFile is not null && File.Exists(envFile) ? (dir, ReadEnv(envFile)) : (null, []);
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

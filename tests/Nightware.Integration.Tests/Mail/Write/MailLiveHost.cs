using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Nightware.Integration.Tests.Directory;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>
/// An in-process Nightware for the mail tests, configured like <see cref="LiveStack"/>'s, whose outgoing
/// clients trust the stack's Caddy CA. The trust is added as a handler-builder action after every named
/// client is configured: the adapters set their own primary handlers (cookie-free, no redirects), which
/// replace one configured through <c>ConfigureHttpClientDefaults</c>.
/// </summary>
internal sealed class MailLiveHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly X509Certificate2 _ca;
    private readonly LiveStack _stack;

    public MailLiveHost(LiveStack stack)
    {
        _stack = stack;
        _ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(StalwartProbe.DeployDir(), ".certs", "caddy-root.crt"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = LiveStack.Issuer,
                ["Discovery:GraphBaseUrl"] = LiveStack.GraphBase,
                ["Discovery:GraphBetaBaseUrl"] = "https://graph." + LiveStack.Domain + "/beta",
                ["DeltaLinks:SigningKey"] = "integration",
                ["Directory:KeycloakBaseUrl"] = "https://auth." + LiveStack.Domain,
                ["Directory:ClientSecret"] = StalwartProbe.Env("NIGHTWARE_API_CLIENT_SECRET"),
                ["Dav:BaseUrl"] = LiveStack.CloudBase + "/",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => Trust(b.PrimaryHandler)));
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    var handler = new SocketsHttpHandler();
                    Trust(handler);
                    o.BackchannelHttpHandler = handler;
                });
            });
        });
    }

    /// <summary>A client of the in-process Nightware carrying <paramref name="user"/>'s NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphClientAsync(string user, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _stack.UserTokenAsync(user, cancellationToken));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _ca.Dispose();
    }

    private void Trust(HttpMessageHandler handler)
    {
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
        }
        else if (handler is HttpClientHandler classic)
        {
            classic.ServerCertificateCustomValidationCallback = (_, certificate, presented, errors) => IsTrusted(certificate, presented, errors);
        }
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
}

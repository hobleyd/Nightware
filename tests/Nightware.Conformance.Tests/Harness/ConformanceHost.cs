using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Api.Composition;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>
/// The real <c>Program</c>, with fake authentication (any <c>Authorization</c> header is
/// <see cref="UserEmail"/>), the arrangement's port fakes, and Graph's own host as the public base
/// URL so emitted links line up with the fixtures'.
/// </summary>
public sealed class ConformanceHost : WebApplicationFactory<Program>
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";
    public const string GraphBetaBase = "https://graph.microsoft.com/beta";
    public const string UserEmail = "me@example.com";

    private readonly PortFakes _fakes;
    private readonly ConformanceArrangement? _arrangement;

    public ConformanceHost(PortFakes fakes, ConformanceArrangement? arrangement = null)
    {
        _fakes = fakes;
        _arrangement = arrangement;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(static l => l.ClearProviders());
        builder.ConfigureAppConfiguration(static (_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = string.Empty,
            ["Discovery:GraphBaseUrl"] = GraphBase,
            ["Discovery:GraphBetaBaseUrl"] = GraphBetaBase,
            ["DeltaLinks:SigningKey"] = "conformance",
            ["Directory:ClientSecret"] = "conformance",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FakeBearerHandler>(FakeBearerHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(static o => o.DefaultAuthenticateScheme = FakeBearerHandler.SchemeName);
            _fakes.Register(services);
            _arrangement?.Services?.Invoke(services);
            if (_arrangement?.MapEndpoints is { } map)
            {
                services.AddSingleton<IGraphEndpointContributor>(new Contributor(map));
            }
        });
    }

    /// <summary>Authenticates any request that carries an <c>Authorization</c> header as the test user.</summary>
    private sealed class FakeBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "ConformanceBearer";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim("sub", "conformance-user"),
                    new Claim("email", UserEmail),
                    new Claim("preferred_username", "me"),
                    new Claim("name", "Me Example"),
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class Contributor(Action<RouteGroupBuilder> map) : IGraphEndpointContributor
    {
        public void Map(RouteGroupBuilder graph) => map(graph);
    }
}

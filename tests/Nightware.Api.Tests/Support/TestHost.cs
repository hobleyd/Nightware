using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Nightware.Api.Composition;

namespace Nightware.Api.Tests.Support;

/// <summary>
/// An in-memory Nightware host built exactly like Program (AddNightware/UseNightware), with a static
/// JWT signing key (no metadata fetch) and room for test-only endpoints and service overrides.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    public const string Issuer = "https://issuer.test/realms/nightware";
    public const string Audience = "nightware-api";
    public const string GraphBase = "https://graph.test.example/v1.0";
    public const string GraphBetaBase = "https://graph.test.example/beta";
    public const string UserEmail = "me@example.com";

    public static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("nightware-tests-signing-key-0123456789abcdef"));

    private TestHost(WebApplication app)
    {
        App = app;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<TestHost> StartAsync(
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? map = null,
        IDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(DefaultConfiguration());
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.Services.AddNightware(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, static o =>
        {
            o.TokenValidationParameters.IssuerSigningKey = SigningKey;
            o.TokenValidationParameters.ValidIssuer = Issuer;
        });
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseNightware();
        map?.Invoke(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestHost(app);
    }

    public static Dictionary<string, string?> DefaultConfiguration() => new()
    {
        ["Auth:Authority"] = string.Empty,
        ["Auth:Audience"] = Audience,
        ["Auth:RequireHttpsMetadata"] = "false",
        ["Discovery:GraphBaseUrl"] = GraphBase,
        ["Discovery:GraphBetaBaseUrl"] = GraphBetaBase,
        ["Discovery:AuthorizationEndpoint"] = "https://auth.test.example/authorize",
        ["Discovery:TokenEndpoint"] = "https://auth.test.example/token",
        ["Discovery:ClientId"] = "nightmail",
        ["Discovery:Scopes:0"] = "openid",
        ["Discovery:Scopes:1"] = "email",
        ["DeltaLinks:SigningKey"] = "test-delta-key",
    };

    /// <summary>Mints an access token the host accepts.</summary>
    public static string Token(string? email = UserEmail, string sub = "user-1", DateTime? expires = null, string audience = Audience, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = sub,
            ["preferred_username"] = "me",
            ["name"] = "Me Example",
        };
        if (email is not null)
        {
            claims["email"] = email;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Claims = claims,
            Subject = new ClaimsIdentity(),
            NotBefore = (expires ?? DateTime.UtcNow.AddHours(1)).AddHours(-2),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public HttpClient AuthorizedClient(string? token = null)
    {
        var client = App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

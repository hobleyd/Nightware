using System.Net;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Directory.Tests.TestSupport;

/// <summary>A WireMock Keycloak serving realm <c>nightware</c>.</summary>
internal sealed class KeycloakStub : IDisposable
{
    public const string Realm = "nightware";
    public const string ClientId = "nightware-api";
    public const string ClientSecret = "nightware-api-dev-secret";
    public const string TokenPath = "/realms/nightware/protocol/openid-connect/token";
    public const string AdminPath = "/admin/realms/nightware";
    public const string DiscoveryPath = "/realms/nightware/.well-known/openid-configuration";

    public KeycloakStub() => Server = WireMockServer.Start();

    public WireMockServer Server { get; }

    public string Url => Server.Url!;

    public IEnumerable<ILogEntry> TokenRequests => Server.LogEntries.Where(static e => e.RequestMessage?.Path == TokenPath);

    public int ClientCredentialsHits => TokenRequests.Count(static e => e.RequestMessage?.Body?.Contains("grant_type=client_credentials", StringComparison.Ordinal) == true);

    public int ExchangeHits => TokenRequests.Count(static e => e.RequestMessage?.Body?.Contains("token-exchange", StringComparison.Ordinal) == true);

    public IEnumerable<ILogEntry> AdminRequests => Server.LogEntries.Where(static e => e.RequestMessage?.Path?.StartsWith(AdminPath, StringComparison.Ordinal) == true);

    /// <summary>Client-credentials grant answers with a fresh service-account token each time.</summary>
    public void GivenServiceAccountToken(int expiresIn = 900, TimeSpan? delay = null, string? token = null)
    {
        int counter = 0;
        IResponseBuilder response = Response.Create();
        if (delay is { } d)
        {
            response = response.WithDelay(d);
        }

        Server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(static b => b?.Contains("grant_type=client_credentials", StringComparison.Ordinal) == true))
            .AtPriority(10)
            .RespondWith(response
                .WithCallback(_ => new WireMock.ResponseMessage
                {
                    StatusCode = 200,
                    Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
                    BodyData = new WireMock.Util.BodyData
                    {
                        BodyAsString = Fixture.Token("token-client-credentials.json", token ?? $"sa-token-{Interlocked.Increment(ref counter)}")
                            .Replace("\"expires_in\":900", $"\"expires_in\":{expiresIn}", StringComparison.Ordinal),
                        DetectedBodyType = WireMock.Types.BodyType.String,
                    },
                }));
    }

    public void GivenServiceAccountTokenFails(HttpStatusCode status, string? body = null) =>
        Server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(static b => b?.Contains("grant_type=client_credentials", StringComparison.Ordinal) == true))
            .AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body ?? string.Empty));

    /// <summary>Token exchange to <paramref name="audience"/> succeeds with <paramref name="exchanged"/>.</summary>
    public void GivenExchange(string audience, string exchanged, int expiresIn = 900) =>
        Server.Given(Request.Create().WithPath(TokenPath).UsingPost()
                .WithBody(b => b?.Contains("token-exchange", StringComparison.Ordinal) == true
                               && b.Contains($"audience={audience}", StringComparison.Ordinal)))
            .AtPriority(10)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Fixture.Token("token-exchange.json", exchanged).Replace("\"expires_in\":900", $"\"expires_in\":{expiresIn}", StringComparison.Ordinal)));

    public void GivenExchangeFails(HttpStatusCode status, string? body, string? retryAfter = null)
    {
        IResponseBuilder response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body ?? string.Empty);
        if (retryAfter is not null)
        {
            response = response.WithHeader("Retry-After", retryAfter);
        }

        Server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(static b => b?.Contains("token-exchange", StringComparison.Ordinal) == true))
            .AtPriority(5)
            .RespondWith(response);
    }

    public void Dispose() => Server.Stop();
}

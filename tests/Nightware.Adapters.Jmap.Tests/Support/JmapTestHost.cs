using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Jmap.Tests.Support;

/// <summary>
/// The adapter wired exactly as <c>AddJmapAdapter</c> does, pointed at a WireMock "Stalwart".
/// The fixture session advertises https://mail.nightware.localhost URLs; the default
/// <c>UseSessionUrlOrigin</c> moves them onto the WireMock origin, as in the compose stack.
/// </summary>
internal sealed class JmapTestHost : IAsyncDisposable
{
    public const string AliceAddress = "alice@nightware.localhost";

    private readonly ServiceProvider _provider;

    public JmapTestHost(Action<Dictionary<string, string?>>? configure = null, string sessionPath = "/jmap/session", TimeProvider? time = null)
    {
        Server = WireMockServer.Start();
        var settings = new Dictionary<string, string?>
        {
            ["Jmap:SessionUrl"] = $"{Server.Url}{sessionPath}",
            ["Jmap:MaxRetryAttempts"] = "0",
            ["Jmap:RequestTimeout"] = "00:00:05",
            ["Jmap:AttemptTimeout"] = "00:00:05",
        };
        configure?.Invoke(settings);

        Credentials = Substitute.For<IBackendCredentials>();
        UseCredential(new BackendCredential("Bearer", "alice-token", CredentialStrategy.TokenExchange));
        UserContext = Substitute.For<IUserContext>();
        UserContext.Principal.Returns(new Principal("u-alice", AliceAddress, AliceAddress, "Alice"));

        var services = new ServiceCollection();
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.AddJmapAdapter(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddScoped(_ => Credentials);
        services.AddScoped(_ => UserContext);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public WireMockServer Server { get; }

    public IBackendCredentials Credentials { get; }

    public IUserContext UserContext { get; }

    public IServiceProvider Services => _provider;

    public void UseCredential(BackendCredential credential) =>
        Credentials.ForAsync(Backend.Mail, Arg.Any<CancellationToken>()).Returns(Result<BackendCredential>.Success(credential));

    /// <summary>A fresh DI scope's client (one Graph request).</summary>
    public IJmapClient Client() => _provider.CreateScope().ServiceProvider.GetRequiredService<IJmapClient>();

    public IJmapBlobService Blobs() => _provider.CreateScope().ServiceProvider.GetRequiredService<IJmapBlobService>();

    public void StubSession(string? json = null, string path = "/jmap/session") =>
        Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(json ?? Fixtures.AliceSession));

    /// <summary>The Alice session with <c>state</c> and core limits overridden.</summary>
    public static string Session(string state = "3e25b2a0", int? maxObjectsInGet = null, int? maxCallsInRequest = null)
    {
        var document = JsonNode.Parse(Fixtures.AliceSession)!.AsObject();
        document["state"] = state;
        var core = document["capabilities"]!["urn:ietf:params:jmap:core"]!.AsObject();
        if (maxObjectsInGet is not null)
        {
            core["maxObjectsInGet"] = maxObjectsInGet;
        }

        if (maxCallsInRequest is not null)
        {
            core["maxCallsInRequest"] = maxCallsInRequest;
        }

        return document.ToJsonString();
    }

    /// <summary>The Alice session without <paramref name="capability"/>, at session and account level.</summary>
    public static string SessionWithout(string capability)
    {
        var document = JsonNode.Parse(Fixtures.AliceSession)!.AsObject();
        document["capabilities"]!.AsObject().Remove(capability);
        foreach (var (_, account) in document["accounts"]!.AsObject())
        {
            account!["accountCapabilities"]!.AsObject().Remove(capability);
        }

        return document.ToJsonString();
    }

    /// <summary>The Alice session with <c>urn:ietf:params:jmap:metadata</c> advertised for her account, as Stalwart 1.1 does.</summary>
    public static string SessionWithMetadata()
    {
        var document = JsonNode.Parse(Fixtures.AliceSession)!.AsObject();
        const string metadata = "urn:ietf:params:jmap:metadata";
        document["capabilities"]![metadata] = new JsonObject();
        foreach (var (_, account) in document["accounts"]!.AsObject())
        {
            account!["accountCapabilities"]![metadata] = new JsonObject();
        }

        return document.ToJsonString();
    }

    /// <summary>Answers every API POST with <paramref name="methodResponses"/> (a JSON array literal).</summary>
    public void StubApi(string methodResponses, string sessionState = "3e25b2a0", string? createdIds = null) =>
        Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(ApiResponse(methodResponses, sessionState, createdIds));

    public static IResponseBuilder ApiResponse(string methodResponses, string sessionState = "3e25b2a0", string? createdIds = null) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
            .WithBody($"{{\"methodResponses\":{methodResponses},\"sessionState\":\"{sessionState}\"{(createdIds is null ? string.Empty : $",\"createdIds\":{createdIds}")}}}");

    public IReadOnlyList<ILogEntry> Requests(string path, string method) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Path == path && string.Equals(e.RequestMessage!.Method, method, StringComparison.OrdinalIgnoreCase))];

    public IReadOnlyList<JsonObject> ApiBodies() =>
        [.. Requests("/jmap/", "POST").Select(e => JsonNode.Parse(e.RequestMessage!.Body!)!.AsObject())];

    public static string? Authorization(ILogEntry entry) =>
        entry.RequestMessage!.Headers is { } headers && headers.TryGetValue("Authorization", out var values) ? values.FirstOrDefault() : null;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        Server.Stop();
        Server.Dispose();
    }
}

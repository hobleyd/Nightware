using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Adapters.Jmap.Administration;

using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Jmap.Tests.Support;

/// <summary>
/// <c>AddStalwartAdministration</c> alone (no user context, no backend credentials), pointed at a
/// WireMock "Stalwart" management API at <c>/jmap/</c>, with every log line captured.
/// </summary>
internal sealed class StalwartManagementTestHost : IAsyncDisposable
{
    public const string Username = "nightware-admin@nightware.localhost";
    public const string Password = "s3cret-management-PASSWORD";
    public const string ApiKey = "API_s3cret-management-KEY";

    private readonly ServiceProvider _provider;

    public StalwartManagementTestHost(Action<Dictionary<string, string?>>? configure = null)
    {
        Server = WireMockServer.Start();
        var settings = new Dictionary<string, string?>
        {
            ["StalwartManagement:ApiUrl"] = $"{Server.Url}/jmap/",
            ["StalwartManagement:Username"] = Username,
            ["StalwartManagement:Password"] = Password,
            ["StalwartManagement:MaxRetryAttempts"] = "0",
            ["StalwartManagement:RequestTimeout"] = "00:00:05",
            ["StalwartManagement:AttemptTimeout"] = "00:00:05",
        };
        configure?.Invoke(settings);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddStalwartAdministration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        // No lists unless a test says otherwise: an address no account holds is looked for among the lists too.
        Stub("x:MailingList/query", NoLists, priority: 1000);
    }

    public const string NoLists = """[["x:MailingList/query",{"ids":[],"position":0,"queryState":"q"},"c0"],["x:MailingList/get",{"state":"s","list":[],"notFound":[]},"c1"]]""";

    public WireMockServer Server { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _provider;

    /// <summary>A service from a fresh scope (one console request).</summary>
    public T Get<T>()
        where T : notnull => _provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    /// <summary>Answers POSTs whose first method call is <paramref name="firstMethod"/>.</summary>
    public void Stub(string firstMethod, string methodResponses, int priority = 10) =>
        Server.Given(Request.Create().WithPath("/jmap/").UsingPost().WithBody(new WildcardMatcher($"*\"methodCalls\":[[\"{firstMethod}\"*")))
            .AtPriority(priority)
            .RespondWith(Ok(methodResponses));

    /// <summary>Answers POSTs whose first call is <paramref name="firstMethod"/> and whose body contains <paramref name="fragment"/>.</summary>
    public void StubWhen(string firstMethod, string fragment, string methodResponses, int priority = 1) =>
        Server.Given(Request.Create().WithPath("/jmap/").UsingPost()
                .WithBody(new WildcardMatcher($"*\"methodCalls\":[[\"{firstMethod}\"*{fragment}*")))
            .AtPriority(priority)
            .RespondWith(Ok(methodResponses));

    public void StubStatus(int status, string? body = null, string contentType = "application/json") =>
        Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body ?? string.Empty));

    public static IResponseBuilder Ok(string methodResponses) =>
        Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
            .WithBody($"{{\"methodResponses\":{methodResponses},\"sessionState\":\"s1\"}}");

    /// <summary>The domain lookup's answer: <c>x:Domain/query</c> + <c>x:Domain/get</c>.</summary>
    public static string DomainLookup(params (string Id, string Name)[] domains) => DomainLookup(domains, extra: null);

    public static string DomainLookup((string Id, string Name)[] domains, string? extra)
    {
        string ids = string.Join(',', domains.Select(d => $"\"{d.Id}\""));
        string list = string.Join(',', domains.Select(d => $"{{\"id\":\"{d.Id}\",\"name\":\"{d.Name}\"{extra}}}"));
        return $$"""[["x:Domain/query",{"ids":[{{ids}}],"position":0,"queryState":"q"},"c0"],["x:Domain/get",{"state":"s","list":[{{list}}],"notFound":[]},"c1"]]""";
    }

    public static string AccountPage(params string[] accounts)
    {
        string ids = string.Join(',', accounts.Select(a => JsonNode.Parse(a)!["id"]!.ToJsonString()));
        return $$"""[["x:Account/query",{"ids":[{{ids}}],"position":0,"queryState":"q"},"c0"],["x:Account/get",{"state":"s","list":[{{string.Join(',', accounts)}}],"notFound":[]},"c1"]]""";
    }

    public IReadOnlyList<JsonObject> Bodies() =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Path == "/jmap/").Select(e => JsonNode.Parse(e.RequestMessage!.Body!)!.AsObject())];

    /// <summary>Every method call sent, in order, as <c>[name, arguments, callId]</c>.</summary>
    public IReadOnlyList<JsonArray> Calls() => [.. Bodies().SelectMany(b => b["methodCalls"]!.AsArray().Select(c => c!.AsArray()))];

    public IReadOnlyList<JsonArray> Calls(string method) => [.. Calls().Where(c => c[0]!.GetValue<string>() == method)];

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        Server.Stop();
        Server.Dispose();
    }
}
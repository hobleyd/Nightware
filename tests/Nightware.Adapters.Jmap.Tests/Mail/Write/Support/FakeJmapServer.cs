using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Types;
using WireMock.Util;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write.Support;

/// <summary>
/// A WireMock "Stalwart" that answers each method call by name, so a port that sends several requests
/// (look up, then write) can be tested end to end. Responses keep the request's call ids; an unhandled
/// method answers <c>serverFail</c>, so a missing arrangement fails loudly.
/// </summary>
internal sealed class FakeJmapServer : IAsyncDisposable
{
    public const string Account = "c";

    private readonly List<Handler> _handlers = [];

    /// <param name="session">The session document (default: the recorded Alice session).</param>
    public FakeJmapServer(string? session = null)
    {
        Host = new JmapTestHost();
        Host.StubSession(session);
        Host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithCallback(Answer));
        Locator = Substitute.For<IJmapMessageLocator>();
    }

    public JmapTestHost Host { get; }

    /// <summary>The Phase 2 seam, mocked: arrange it with <see cref="Locate"/>.</summary>
    public IJmapMessageLocator Locator { get; }

    public IJmapClient Client() => Host.Client();

    public IJmapBlobService Blobs() => Host.Blobs();

    public IUserContext User => Host.UserContext;

    /// <summary>Makes <paramref name="id"/> resolve to <paramref name="jmapId"/>.</summary>
    public void Locate(string id, string jmapId) =>
        Locator.ResolveAsync(Arg.Is<JmapAccount>(a => a.AccountId == Account), new MessageId(id), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success(jmapId));

    /// <summary>Answers <paramref name="method"/> (when <paramref name="when"/> matches its arguments) with <paramref name="arguments"/>.</summary>
    public FakeJmapServer On(string method, string arguments, Func<JsonObject, bool>? when = null) =>
        On(method, _ => [(method, JsonNode.Parse(arguments)!)], when);

    /// <summary>Answers successive calls to <paramref name="method"/> with each response in turn; the last one repeats.</summary>
    public FakeJmapServer OnEach(string method, params string[] responses)
    {
        var next = 0;
        return On(method, _ =>
        {
            var response = responses[Math.Min(next, responses.Length - 1)];
            next++;
            return [(method, JsonNode.Parse(response)!)];
        });
    }

    /// <summary>Answers <paramref name="method"/> with a fixture from <c>Fixtures/Mail/Write</c>.</summary>
    public FakeJmapServer OnFixture(string method, string fixture, Func<JsonObject, bool>? when = null) =>
        On(method, Read(fixture), when);

    /// <summary>Answers with several responses under the call's id (e.g. the implicit Email/set after a submission).</summary>
    public FakeJmapServer On(string method, Func<JsonObject, IEnumerable<(string Name, JsonNode Arguments)>> respond, Func<JsonObject, bool>? when = null)
    {
        // Later arrangements win, as with NSubstitute.
        _handlers.Insert(0, new Handler(method, when ?? (static _ => true), respond));
        return this;
    }

    public void StubUpload(string blobId, string type, long size) =>
        Host.Server.Given(Request.Create().WithPath($"/jmap/upload/{Account}/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"accountId":"{{Account}}","blobId":"{{blobId}}","type":"{{type}}","size":{{size}}}"""));

    /// <summary>Every method call sent, in order: (method, arguments).</summary>
    public IReadOnlyList<(string Method, JsonObject Arguments)> Calls() =>
        [.. Host.ApiBodies().SelectMany(static b => b["methodCalls"]!.AsArray())
            .Select(static c => (c![0]!.GetValue<string>(), c[1]!.AsObject()))];

    /// <summary>The arguments of the only call to <paramref name="method"/>.</summary>
    public JsonObject Single(string method) => Calls().Where(c => c.Method == method).ShouldHaveSingleItem().Arguments;

    /// <summary>The HTTP requests sent to the API, each as its method names.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Requests() =>
        [.. Host.ApiBodies().Select(static b => (IReadOnlyList<string>)[.. b["methodCalls"]!.AsArray().Select(static c => c![0]!.GetValue<string>())])];

    public byte[] UploadedBody() =>
        Host.Requests($"/jmap/upload/{Account}/", "POST").ShouldHaveSingleItem().RequestMessage!.BodyAsBytes!;

    public static string Read(string fixture) => Fixtures.Read(Path.Combine("Mail", "Write", fixture));

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    private ResponseMessage Answer(IRequestMessage request)
    {
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        var responses = new JsonArray();
        foreach (var call in body["methodCalls"]!.AsArray())
        {
            var method = call![0]!.GetValue<string>();
            var arguments = call[1]!.AsObject();
            var callId = call[2]!.GetValue<string>();
            var handler = _handlers.FirstOrDefault(h => h.Method == method && h.When(arguments));
            if (handler is null)
            {
                responses.Add(new JsonArray("error", new JsonObject { ["type"] = "serverFail", ["description"] = $"No fake for {method}" }, callId));
                continue;
            }

            foreach (var (name, args) in handler.Respond(arguments))
            {
                responses.Add(new JsonArray(name, args.DeepClone(), callId));
            }
        }

        var json = new JsonObject { ["methodResponses"] = responses, ["sessionState"] = "3e25b2a0" }.ToJsonString();
        return new ResponseMessage
        {
            StatusCode = 200,
            Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = new("application/json") },
            BodyData = new BodyData { BodyAsString = json, DetectedBodyType = BodyType.String },
        };
    }

    private sealed record Handler(string Method, Func<JsonObject, bool> When, Func<JsonObject, IEnumerable<(string Name, JsonNode Arguments)>> Respond);
}

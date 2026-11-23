using System.Text.Json;

using Microsoft.Extensions.Options;

using TalkOptions = Nightware.Adapters.Dav.Talk.TalkOptions;
using Nightware.Adapters.Dav.Chats;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>
/// A WireMock Nextcloud answering Talk's OCS API, with the chat adapter wired to it. Replies are shaped as Talk 25.0.8's
/// (Nextcloud 35); the fixtures under <c>Fixtures/Chats</c> were recorded from the dev stack (host rewritten to
/// <c>cloud.example.com</c>). Directory users: alice and bob, with directory ids <c>id-alice</c> and <c>id-bob</c>.
/// </summary>
internal sealed class TalkTestServer : IDisposable
{
    public const string Ocs = "/ocs/v2.php/";
    public const string Rooms = Ocs + TalkChatClient.RoomsPath;
    public const string Chat = Ocs + TalkChatClient.ChatPath;
    public const string Reaction = Ocs + TalkChatClient.ReactionPath;
    public const string PublicBase = "https://cloud.example.com/";

    public static readonly DirectoryUser Alice = new("id-alice", "Alice Anderson", "alice@example.com") { Mail = "alice@example.com" };
    public static readonly DirectoryUser Bob = new("id-bob", "Bob Brown", "bob@example.com") { Mail = "bob@example.com" };

    private readonly DavTestServer _dav = new();

    public TalkTestServer(bool links = true)
    {
        Names = Substitute.For<IUserAccountNames>();
        Names.UserOfAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<string>() switch
            {
                "alice" => Alice,
                "bob" => Bob,
                _ => Result<DirectoryUser>.Failure(Error.NotFound("nobody")),
            });
        Names.AccountNameOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => c.Arg<string>() switch
            {
                "id-alice" or "alice@example.com" => "alice",
                "id-bob" or "bob@example.com" => "bob",
                _ => Result<string>.Failure(Error.NotFound("nobody")),
            });
        Client = new TalkChatClient(_dav.Client);
        Identities = new TalkIdentities(Names);
        Feed = new TalkMessageFeed(Client, Identities);
        Links = new TalkLinks(Options.Create(new TalkOptions { PublicBaseUrl = links ? new Uri(PublicBase) : null }));
        ChatRooms = new TalkChatRooms(Client, Links);
        Channels = new TalkChannelRooms(_dav.Client);
    }

    public IUserAccountNames Names { get; }

    public TalkChatClient Client { get; }

    public TalkIdentities Identities { get; }

    public TalkMessageFeed Feed { get; }

    public TalkLinks Links { get; }

    public TalkChatRooms ChatRooms { get; }

    public TalkChannelRooms Channels { get; }

    public WireMock.Server.WireMockServer Server => _dav.Server;

    public IReadOnlyList<ILogEntry> Requests => [.. _dav.Server.LogEntries];

    public ILogEntry Last => _dav.Server.LogEntries[^1];

    public TalkChatStore Store() => new(Client, ChatRooms, Channels, Feed, Identities);

    public TalkChatMembers Members() => new(Client, Identities);

    public TalkChatMessages Messages() => new(Client, ChatRooms, Feed);

    public TalkChatPins Pins() => new(Client, ChatRooms, Feed);

    /// <summary>Answers <paramref name="method"/> <paramref name="path"/> with an OCS envelope around <paramref name="data"/>.</summary>
    public void Reply(string method, string path, int status, string data = "[]", int? ocsStatus = null, IDictionary<string, string>? headers = null, int priority = 10)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json; charset=utf-8");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(name, value);
        }

        var code = ocsStatus ?? status;
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(response
            .WithBody($$$"""{"ocs":{"meta":{"status":"{{{(code < 300 ? "ok" : "failure")}}}","statuscode":{{{code}}},"message":""},"data":{{{data}}}}}"""));
    }

    /// <summary>Answers with a recorded fixture as it is (a whole OCS envelope).</summary>
    public void ReplyFixture(string method, string path, string fixture, IDictionary<string, string>? headers = null, int priority = 10)
    {
        var response = Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json; charset=utf-8").WithBody(Fixture.Read("Chats/" + fixture));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(name, value);
        }

        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(response);
    }

    /// <summary>Talk's "nothing (more)": 304 with no body.</summary>
    public void NotModified(string method, string path, int priority = 10) =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(Response.Create().WithStatusCode(304));

    /// <summary>One recorded room as an OCS room answer.</summary>
    public static string RoomData(string token)
    {
        using var rooms = JsonDocument.Parse(Fixture.Read("Chats/rooms.json"));
        return rooms.RootElement.GetProperty("ocs").GetProperty("data").EnumerateArray().Single(r => r.GetProperty("token").GetString() == token).GetRawText();
    }

    /// <summary>The recorded messages of the probe group room, by id.</summary>
    public static TalkMessage Recorded(long id, string fixture = "chat-history.json") =>
        RecordedMessages(fixture).Single(m => m.Id == id);

    public static IReadOnlyList<TalkMessage> RecordedMessages(string fixture = "chat-history.json")
    {
        using var history = JsonDocument.Parse(Fixture.Read("Chats/" + fixture));
        return TalkMessage.ParseMany(history.RootElement.GetProperty("ocs").GetProperty("data"));
    }

    public static string? Query(ILogEntry entry, string name) =>
        entry.RequestMessage!.Query is { } query && query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public void Dispose() => _dav.Dispose();
}

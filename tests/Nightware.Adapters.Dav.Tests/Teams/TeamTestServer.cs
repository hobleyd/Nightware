using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Adapters.Dav.Teams;
using Nightware.Adapters.Dav.Tests.Chats;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Teams;

/// <summary>
/// A WireMock Nextcloud answering the circles app's and Talk's OCS APIs, with the teams adapter wired to it. The
/// fixtures under <c>Fixtures/Teams</c> were recorded from the dev stack (Nextcloud 35, circles 35, Talk 25.0.8; host
/// rewritten to <c>cloud.example.com</c>): alice owns team <see cref="TeamId"/> (bob is a member) with the rooms General
/// (<see cref="General"/>, room id 39) and Design (<see cref="Design"/>, 40), and a thread 209 in General.
/// </summary>
internal sealed class TeamTestServer : IDisposable
{
    public const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";
    public const string OtherTeamId = "Hpj1aAWPsDDAZh4UF1EwHuz86oXYdxv";
    public const string AliceMember = "LZ3QVwCD7nuUSNFdJYckPyiZN8OnTjH";
    public const string BobMember = "zjioHBheH4iNehqhIX2yQrHrn6RVDCd";
    public const string General = "xtugjbpy";
    public const string Design = "hktd6xvp";

    public const string Circles = TalkTestServer.Ocs + NextcloudCircles.CirclesPath;
    public const string Circle = Circles + "/" + TeamId;
    public const string Resources = TalkTestServer.Ocs + "teams/" + TeamId + "/resources";
    public const string Folder = TalkTestServer.Ocs + NextcloudCircles.TeamsPath + "/" + TeamId + "/folder";

    public TeamTestServer(bool links = true)
    {
        Talk = new TalkTestServer(links);
        CirclesClient = new NextcloudCircles(Talk.Dav);
        Channels = new TalkChannelStore(CirclesClient, Talk.Client, Talk.Links);
    }

    public TalkTestServer Talk { get; }

    public NextcloudCircles CirclesClient { get; }

    public TalkChannelStore Channels { get; }

    public IReadOnlyList<ILogEntry> Requests => Talk.Requests;

    public NextcloudTeamStore Store() => new(CirclesClient, Talk.Client, Talk.Identities);

    public NextcloudTeamMembers Members() => new(CirclesClient, Talk.Identities);

    public TalkChannelMessages Messages() => new(Talk.Client, Channels, Talk.Feed);

    public void Reply(string method, string path, int status, string data = "[]", int? ocsStatus = null, IDictionary<string, string>? headers = null, int priority = 10) =>
        Talk.Reply(method, path, status, data, ocsStatus, headers, priority);

    /// <summary>Answers with a recorded fixture from <c>Fixtures/Teams</c> as it is.</summary>
    public void ReplyFixture(string method, string path, string fixture, IDictionary<string, string>? headers = null, int priority = 10)
    {
        var response = Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json; charset=utf-8").WithBody(Fixture.Read("Teams/" + fixture));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(name, value);
        }

        Talk.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).AtPriority(priority).RespondWith(response);
    }

    /// <summary>Answers a request whose query has <paramref name="name"/>=<paramref name="value"/> (before any other answer for the path).</summary>
    public void ReplyFixtureWhen(string method, string path, string name, string value, string fixture, int priority = 5)
    {
        var response = Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json; charset=utf-8").WithBody(Fixture.Read("Teams/" + fixture));
        Talk.Server.Given(Request.Create().WithPath(path).UsingMethod(method).WithParam(name, value)).AtPriority(priority).RespondWith(response);
    }

    /// <summary>Talk's "nothing (more)" (a bodiless 304) for a request whose query has <paramref name="name"/>=<paramref name="value"/>.</summary>
    public void NotModifiedWhen(string path, string name, string value, int priority = 1) =>
        Talk.Server.Given(Request.Create().WithPath(path).UsingMethod("GET").WithParam(name, value)).AtPriority(priority).RespondWith(Response.Create().WithStatusCode(304));

    /// <summary>The recorded team: its circle in the caller's list, its resources and the caller's rooms.</summary>
    public void RecordedTeam()
    {
        ReplyFixture("GET", Circles, "circles.json");
        ReplyFixture("GET", Resources, "resources.json");
        ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
    }

    /// <summary>One recorded room as an OCS room answer.</summary>
    public static string RoomData(string token)
    {
        using var rooms = System.Text.Json.JsonDocument.Parse(Fixture.Read("Teams/rooms.json"));
        return rooms.RootElement.GetProperty("ocs").GetProperty("data").EnumerateArray().Single(r => r.GetProperty("token").GetString() == token).GetRawText();
    }

    /// <summary>One recorded message of General (from its history), as Talk's context call answers it.</summary>
    public static string MessageData(long id)
    {
        using var history = System.Text.Json.JsonDocument.Parse(Fixture.Read("Teams/channel-history.json"));
        return "[" + history.RootElement.GetProperty("ocs").GetProperty("data").EnumerateArray().Single(m => m.GetProperty("id").GetInt64() == id).GetRawText() + "]";
    }

    public IEnumerable<ILogEntry> To(string method, string path) =>
        Requests.Where(r => r.RequestMessage!.Method == method && r.RequestMessage.AbsolutePath == path);

    public static string Body(ILogEntry entry) => entry.RequestMessage!.Body ?? string.Empty;

    public void Dispose() => Talk.Dispose();
}

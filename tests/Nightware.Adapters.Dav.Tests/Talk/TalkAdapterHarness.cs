using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Talk;

/// <summary>
/// Phase 26's Talk adapters through the Dav adapter's real registration, against a WireMock Nextcloud at <c>/cloud/</c>.
/// The OCS replies are Talk 25.0.8's (Nextcloud 35) as the dev stack gave them (phase 26 Step 0), scrubbed to example.com.
/// The directory knows alice (<c>alice-id</c>) and bob (<c>bob-id</c>).
/// </summary>
internal sealed class TalkAdapterHarness : IDisposable
{
    public const string DavRoot = "/cloud/remote.php/dav/";
    public const string Spreed = "/cloud/ocs/v2.php/apps/spreed/api/";
    public const string Rooms = Spreed + "v4/room";
    public const string Calls = Spreed + "v4/call/";
    public const string Chat = Spreed + "v1/chat/";
    public const string Public = "https://cloud.example.com/cloud/";
    public const string AliceCalendar = DavRoot + "calendars/alice/personal/";
    public const string SealingKey = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private readonly ServiceProvider _provider;

    public TalkAdapterHarness(bool configured = true, string callerId = "alice-id")
    {
        // Alice's calendars, where a meeting's event lives (ADR 0009 D7): her personal calendar and the read-only birthdays.
        var alice = new DavPrincipal(DavRoot + "principals/users/alice/", true)
        {
            CalendarHomeSet = DavRoot + "calendars/alice/",
            EmailAddresses = ["alice@example.com"],
        };
        var discovery = Substitute.For<IDavPrincipalDiscovery>();
        discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(alice);
        discovery.DiscoverAsync(new MailboxRef("alice@example.com"), Arg.Any<CancellationToken>()).Returns(alice);
        var catalog = Substitute.For<IDavCollectionCatalog>();
        catalog.ListCalendarsAsync(alice, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavCalendar>>.Success([
            new DavCalendar(AliceCalendar, "Personal") { Components = new HashSet<string>(["VEVENT"]) },
            new DavCalendar(DavRoot + "calendars/alice/contact_birthdays/", "Birthdays") { Components = new HashSet<string>(["VEVENT"]), IsReadOnly = true },
        ]));
        var credentials = Substitute.For<IBackendCredentials>();
        credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>()).Returns(new BackendCredential("Bearer", "nc-token", CredentialStrategy.TokenExchange));
        var user = Substitute.For<IUserContext>();
        user.Principal.Returns(new Principal(callerId, "alice@example.com", "alice@example.com", "Alice Anderson"));

        Names = Substitute.For<IUserAccountNames>();
        Names.AccountNameOfAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("nobody"));
        Names.UserOfAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("nobody"));
        foreach (var (uid, id, name) in new[] { ("alice", "alice-id", "Alice Anderson"), ("bob", "bob-id", "Bob Brown") })
        {
            Names.AccountNameOfAsync(id, Arg.Any<CancellationToken>()).Returns(uid);
            Names.AccountNameOfAsync($"{uid}@example.com", Arg.Any<CancellationToken>()).Returns(uid);
            Names.UserOfAccountAsync(uid, Arg.Any<CancellationToken>()).Returns(new DirectoryUser(id, name, $"{uid}@example.com"));
        }

        var settings = new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = Server.Url + "/cloud/",
            ["Dav:MaxRetryAttempts"] = "0",
        };
        if (configured)
        {
            settings["Talk:PublicBaseUrl"] = Public;
            settings["Calls:SealingKey"] = SealingKey;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => credentials);
        services.AddScoped(_ => discovery);
        services.AddScoped(_ => catalog);
        services.AddScoped(_ => user);
        services.AddSingleton(Names);
        services.AddDavAdapter(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public WireMockServer Server { get; } = WireMockServer.Start();

    public IUserAccountNames Names { get; }

    /// <summary>A fresh request scope's service.</summary>
    public T Get<T>()
        where T : notnull =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    /// <summary>An OCS reply: HTTP and OCS status alike (as OCS v2 answers), <paramref name="data"/> as <c>ocs.data</c>.</summary>
    public void Ocs(string method, string path, int status, string data = "[]", Func<IRequestBuilder, IRequestBuilder>? refine = null, params (string Name, string Value)[] headers)
    {
        var request = Request.Create().WithPath(path).UsingMethod(method);
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json; charset=utf-8")
            .WithBody($$$"""{"ocs":{"meta":{"status":"{{{(status < 300 ? "ok" : "failure")}}}","statuscode":{{{status}}},"message":""},"data":{{{data}}}}}""");
        foreach (var (name, value) in headers)
        {
            response = response.WithHeader(name, value);
        }

        Server.Given(refine is null ? request : refine(request)).RespondWith(response);
    }

    public void Dav(string method, string path, int status, string body, string contentType = "application/xml; charset=utf-8", string? depth = null)
    {
        var request = Request.Create().WithPath(path).UsingMethod(method);
        Server.Given(depth is null ? request : request.WithHeader("Depth", depth))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));
    }

    public IReadOnlyList<ILogEntry> Requests(string method, string? path = null) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Method == method && (path is null || e.RequestMessage.Path == path))];

    public void Dispose()
    {
        _provider.Dispose();
        Server.Stop();
        Server.Dispose();
    }

    /// <summary>Alice's public room <c>abcd1234</c>, bound to 2026-12-01 09:00–10:00 UTC; <paramref name="extra"/> overrides members.</summary>
    public static string Room(string token = "abcd1234", int type = 3, int participantType = 1, string objectType = "event", string objectId = "1796115600#1796119200", string extra = "") =>
        $$"""{"id":12,"token":"{{token}}","type":{{type}},"name":"Q3 review","displayName":"Q3 review","objectType":"{{objectType}}","objectId":"{{objectId}}","participantType":{{participantType}},"hasPassword":false,"hasCall":false,"callStartTime":0{{extra}}}""";

    public const string Participants = """
        [{"actorType":"users","actorId":"alice","displayName":"Alice Anderson","participantType":1,"attendeeId":22,"inCall":0,"sessionIds":[]},
         {"actorType":"users","actorId":"bob","displayName":"Bob Brown","participantType":3,"attendeeId":23,"inCall":0,"sessionIds":[]},
         {"actorType":"emails","actorId":"x","displayName":"Carol Guest","participantType":4,"attendeeId":24,"inCall":0,"sessionIds":[]},
         {"actorType":"groups","actorId":"staff","displayName":"Staff","participantType":3,"attendeeId":25,"inCall":0,"sessionIds":[]}]
        """;

    public const string Created = """
        [{"id":88,"token":"abcd1234","actorType":"users","actorId":"alice","actorDisplayName":"Alice Anderson","timestamp":1795427296,"message":"You created the conversation","messageParameters":{},"systemMessage":"conversation_created","messageType":"system"}]
        """;

    /// <summary>The OCS routes a meeting read uses: the room, its participants and its first chat message.</summary>
    public void ServeMeeting(string room)
    {
        Ocs("GET", Rooms + "/abcd1234", 200, room);
        Ocs("GET", Rooms + "/abcd1234/participants", 200, Participants);
        Ocs("GET", Chat + "abcd1234", 200, Created);
    }
}

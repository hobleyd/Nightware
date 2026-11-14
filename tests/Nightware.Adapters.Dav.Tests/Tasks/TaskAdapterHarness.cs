using System.Text;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Tasks;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Domain.Kernel;
using WireMock.Logging;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Tasks;

/// <summary>
/// The Tasks adapter over a WireMock "Nextcloud": real <see cref="DavClient"/>, catalog and codec;
/// discovery is stubbed to alice (or another principal) so tests focus on the task requests.
/// </summary>
internal sealed class TaskAdapterHarness : IDisposable
{
    public const string Home = "/remote.php/dav/calendars/alice/";
    public const string TasksList = Home + "tasks/";
    public const string SharedList = Home + "team-calendar_shared_by_bob/";
    public const string OpenTask = TasksList + "0f1e2d3c4b5a69788796a5b4c3d2e1f0.ics";
    public const string FilesHome = "/remote.php/dav/files/alice/";

    public static readonly MailboxRef Alice = new("alice@nightware.localhost");

    public static readonly DavPrincipal AlicePrincipal = new("/remote.php/dav/principals/users/alice/", true)
    {
        CalendarHomeSet = Home,
        EmailAddresses = ["alice@nightware.localhost"],
    };

    public TaskAdapterHarness(DavPrincipal? principal = null)
    {
        Discovery = Substitute.For<IDavPrincipalDiscovery>();
        Discovery.DiscoverAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>()).Returns(principal ?? AlicePrincipal);
        Codec = new VTodoCodec(Dav.Time);
        Collections = new DavTaskCollections(Discovery, new DavCollectionCatalog(Dav.Client), Dav.Client, Dav.Paths);
        Store = new DavTaskStore(Collections, Dav.Client, Dav.Paths, Codec);
        Lists = new DavTaskListReader(Collections);
        Files = new TaskAttachmentFiles(Dav.Client, Dav.Paths);
        Attachments = new DavTaskAttachmentStore(Collections, Store, Dav.Client, Codec, Files, Dav.Time);
    }

    public DavTestServer Dav { get; } = new();

    public IDavPrincipalDiscovery Discovery { get; }

    public VTodoCodec Codec { get; }

    public DavTaskCollections Collections { get; }

    public DavTaskStore Store { get; }

    public DavTaskListReader Lists { get; }

    public TaskAttachmentFiles Files { get; }

    public DavTaskAttachmentStore Attachments { get; }

    public static TaskListId ListId(string absoluteHref = TasksList) => DavItemId.Resource(absoluteHref["/remote.php/dav/".Length..]).ToTaskListId();

    public static TaskId TaskIdOf(string absoluteHref = OpenTask) => DavItemId.Resource(absoluteHref["/remote.php/dav/".Length..]).ToTaskId();

    public void Respond(string method, string path, int status, string? body = null, params (string Name, string Value)[] headers)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithBody(body);
        }

        foreach (var (name, value) in headers)
        {
            response = response.WithHeader(name, value);
        }

        Dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(response);
    }

    /// <summary>Answers every <paramref name="method"/> under the Files home (paths there are percent-encoded).</summary>
    public void RespondUnderFiles(string method, int status, string? body = null, params (string Name, string Value)[] headers)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithBody(body);
        }

        foreach (var (name, value) in headers)
        {
            response = response.WithHeader(name, value);
        }

        Dav.Server.Given(Request.Create().WithPath(new WildcardMatcher(FilesHome + "*")).UsingMethod(method)).RespondWith(response);
    }

    public void ServeHome(string fixture = "propfind-calendar-home.xml") =>
        Respond("PROPFIND", Home, 207, Fixture.Read(fixture));

    public void ServeTask(string ics, string path = OpenTask, string etag = "\"etag-1\"") =>
        Respond("GET", path, 200, ics, ("ETag", etag), ("Content-Type", "text/calendar; charset=utf-8"));

    public IReadOnlyList<ILogEntry> Requests(string method) =>
        [.. Dav.Server.LogEntries.Where(e => string.Equals(e.RequestMessage!.Method, method, StringComparison.OrdinalIgnoreCase))];

    public static string BodyOf(ILogEntry entry) =>
        entry.RequestMessage!.Body ?? Encoding.UTF8.GetString(entry.RequestMessage.BodyAsBytes ?? []);

    public static string? Header(ILogEntry entry, string name) =>
        entry.RequestMessage!.Headers is { } headers && headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public static string Ics(string summary = "Reply to Alex about Q2 numbers", string extra = "") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Nightware//EN
        BEGIN:VTODO
        UID:0f1e2d3c4b5a69788796a5b4c3d2e1f0
        CREATED:20261101T080000Z
        LAST-MODIFIED:20261101T080000Z
        DTSTAMP:20261101T080000Z
        SUMMARY:{summary}
        STATUS:NEEDS-ACTION
        DUE;VALUE=DATE:20261105
        {extra}END:VTODO
        END:VCALENDAR
        """.ReplaceLineEndings("\r\n");

    public void Dispose() => Dav.Dispose();
}

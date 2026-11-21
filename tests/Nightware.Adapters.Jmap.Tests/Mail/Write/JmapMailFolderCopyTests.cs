using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

/// <summary>Phase 19: <c>POST mailFolders/{id}/copy</c> over <c>Mailbox/set</c> and <c>Email/import</c>.</summary>
public sealed class JmapMailFolderCopyTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");

    /// <summary>Inbox (a), Projects (h) holding Child (i); the copy lands as h2 holding i2.</summary>
    private const string Tree = """
        {"accountId":"c","state":"s1","list":[
          {"id":"a","name":"Inbox","parentId":null,"role":"inbox","totalEmails":3,"unreadEmails":2},
          {"id":"h","name":"Projects","parentId":null,"role":null,"totalEmails":2,"unreadEmails":1},
          {"id":"i","name":"Child","parentId":"h","role":null,"totalEmails":1,"unreadEmails":0}
        ],"notFound":[]}
        """;

    private const string Copied = """
        {"id":"h2","name":"Projects","parentId":"a","role":null,"totalEmails":2,"unreadEmails":1},
        {"id":"i2","name":"Child","parentId":"h2","role":null,"totalEmails":1,"unreadEmails":0}
        """;

    private const string TreeCreated = """
        {"accountId":"c","oldState":"s1","newState":"s2","created":{"f0":{"id":"h2"},"f1":{"id":"i2"}}}
        """;

    private FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    /// <summary>
    /// Arranges the tree, the copy, and each folder's mail: Projects' page is <paramref name="projectsMail"/> while its
    /// query's position is below <paramref name="projectsPages"/> pages; Child holds one flagged message.
    /// </summary>
    private void Arrange(FakeJmapServer jmap, string projectsMail = """[{"id":"m1","blobId":"b1","keywords":{"$seen":true},"receivedAt":"2026-11-01T10:00:00Z"},{"id":"m2","blobId":"b2","keywords":{},"receivedAt":"2026-11-02T10:00:00Z"}]""", int projectsPages = 1)
    {
        _jmap = jmap;
        (string Mailbox, int Position) queried = ("", 0);
        jmap.OnEach("Mailbox/get", Tree, Tree.Replace("],\"notFound\"", "," + Copied + "],\"notFound\"", StringComparison.Ordinal));
        jmap.On("Mailbox/set", TreeCreated);
        jmap.On("Email/query", args =>
        {
            queried = (args["filter"]!["inMailbox"]!.GetValue<string>(), args["position"]!.GetValue<int>());
            return [("Email/query", JsonNode.Parse("""{"accountId":"c","queryState":"q","canCalculateChanges":false,"position":0,"ids":[]}""")!)];
        });
        jmap.On("Email/get", _ =>
        {
            var list = queried.Mailbox switch
            {
                "h" when queried.Position < projectsPages * JmapMailFolderWriter.CopyPageSize => projectsMail,
                "i" when queried.Position == 0 => """[{"id":"m3","blobId":"b3","keywords":{"$flagged":true},"receivedAt":"2026-11-03T10:00:00Z"}]""",
                _ => "[]",
            };
            return [("Email/get", JsonNode.Parse($$"""{"accountId":"c","state":"e1","list":{{list}},"notFound":[]}""")!)];
        });
        jmap.On("Email/import", args => [("Email/import", new JsonObject
        {
            ["accountId"] = "c",
            ["newState"] = "e2",
            ["created"] = new JsonObject(args["emails"]!.AsObject().Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonObject { ["id"] = "new-" + p.Key }))),
        })]);
    }

    [Fact]
    public async Task Copies_the_subtree_under_the_destination_and_imports_every_message()
    {
        Arrange(new FakeJmapServer());

        var folder = await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct);

        folder.Value.ShouldBe(new MailFolder(new FolderId("h2"), "Projects", new FolderId("a"), 1, 1, 2));
        _jmap.Single("Mailbox/set")["create"]!.ToJsonString().ShouldBe("""{"f0":{"name":"Projects","parentId":"a"},"f1":{"name":"Child","parentId":"#f0"}}""");
        var imports = _jmap.Calls().Where(c => c.Method == "Email/import").Select(c => c.Arguments["emails"]!.AsObject()).ToList();
        imports.Count.ShouldBe(2);
        imports[0]["m0"]!.ToJsonString().ShouldBe("""{"blobId":"b1","mailboxIds":{"h2":true},"keywords":{"$seen":true},"receivedAt":"2026-11-01T10:00:00Z"}""");
        imports[0].Count.ShouldBe(2);
        imports[1]["m0"]!["mailboxIds"]!.ToJsonString().ShouldBe("""{"i2":true}""");
        _jmap.Calls().Where(c => c.Method == "Email/query").Select(c => c.Arguments["filter"]!["inMailbox"]!.GetValue<string>()).ShouldBe(["h", "i"]);
    }

    [Fact]
    public async Task Msgfolderroot_copies_to_the_top_level()
    {
        Arrange(new FakeJmapServer());

        await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("i"), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Ct);

        _jmap.Single("Mailbox/set")["create"]!.ToJsonString().ShouldBe("""{"f0":{"name":"Child"}}""");
    }

    [Fact]
    public async Task A_full_page_reads_the_next_one()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, JmapMailFolderWriter.CopyPageSize).Select(n => "{\"id\":\"p" + n + "\",\"blobId\":\"pb" + n + "\",\"keywords\":{}}")) + "]";
        Arrange(new FakeJmapServer(), full);

        (await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).IsSuccess.ShouldBeTrue();

        // A full page asks for the next one, which comes back empty.
        _jmap.Calls().Where(c => c.Method == "Email/query" && c.Arguments["filter"]!["inMailbox"]!.GetValue<string>() == "h")
            .Select(c => c.Arguments["position"]!.GetValue<int>()).ShouldBe([0, JmapMailFolderWriter.CopyPageSize]);
        _jmap.Calls().Count(c => c.Method == "Email/import").ShouldBe(2);
    }

    [Fact]
    public async Task Metadata_is_carried_to_the_copies()
    {
        Arrange(new FakeJmapServer(JmapTestHost.SessionWithMetadata()),
            """[{"id":"m1","blobId":"b1","keywords":{},"metadata":{"nightware.ms":{"categories":["Red"]}}}]""");
        _jmap.On("Email/set", """{"accountId":"c","oldState":"e2","newState":"e3","updated":{"new-m0":null}}""");

        (await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Calls().Where(c => c.Method == "Email/set").First().Arguments["update"]!.ToJsonString()
            .ShouldBe("""{"new-m0":{"metadata/nightware.ms":{"categories":["Red"]}}}""");
    }

    [Fact]
    public async Task A_name_taken_at_the_destination_is_409_ErrorFolderExists()
    {
        Arrange(new FakeJmapServer());
        _jmap.On("Mailbox/set", """{"accountId":"c","oldState":"s1","newState":"s1","notCreated":{"f0":{"type":"alreadyExists"},"f1":{"type":"invalidProperties"}}}""");

        var result = await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Ct);

        result.Error!.Code.ShouldBe("ErrorFolderExists");
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/import");
    }

    [Theory]
    [InlineData("h")]
    [InlineData("i")]
    public async Task Copying_into_itself_or_a_subfolder_is_400(string destination)
    {
        Arrange(new FakeJmapServer());

        var result = await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.ById(new FolderId(destination)), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Mailbox/set");
    }

    [Fact]
    public async Task An_unknown_source_is_404_and_the_root_is_400()
    {
        Arrange(new FakeJmapServer());
        var writer = new JmapMailFolderWriter(_jmap.Client());

        (await writer.CopyAsync(Alice, new FolderId("zz"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await writer.CopyAsync(Alice, new FolderId("msgfolderroot"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_failed_import_is_returned()
    {
        Arrange(new FakeJmapServer());
        _jmap.On("Email/import", """{"accountId":"c","newState":"e2","notCreated":{"m0":{"type":"overQuota"},"m1":{"type":"overQuota"}}}""");

        var result = await new JmapMailFolderWriter(_jmap.Client()).CopyAsync(Alice, new FolderId("h"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct);

        result.IsSuccess.ShouldBeFalse();
    }
}

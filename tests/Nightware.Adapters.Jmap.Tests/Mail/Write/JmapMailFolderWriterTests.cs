using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapMailFolderWriterTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");

    /// <summary>Alice's recorded mailboxes plus Projects (h) holding Child (i) holding Grandchild (j).</summary>
    private const string Tree = """
        {"accountId":"c","state":"s1","list":[
          {"id":"a","name":"Inbox","parentId":null,"role":"inbox","totalEmails":3,"unreadEmails":2},
          {"id":"b","name":"Deleted Items","parentId":null,"role":"trash","totalEmails":0,"unreadEmails":0},
          {"id":"d","name":"Drafts","parentId":null,"role":"drafts","totalEmails":1,"unreadEmails":0},
          {"id":"h","name":"Projects","parentId":null,"role":null,"totalEmails":12,"unreadEmails":1},
          {"id":"i","name":"Child","parentId":"h","role":null,"totalEmails":0,"unreadEmails":0},
          {"id":"j","name":"Grandchild","parentId":"i","role":null,"totalEmails":0,"unreadEmails":0}
        ],"notFound":[]}
        """;

    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.On("Mailbox/get", Tree);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailFolderWriter Writer() => new(_jmap.Client());

    /// <summary>The tree before the write, then after it (the Mailbox/get in the write's own request).</summary>
    private void After(string added) => _jmap.OnEach("Mailbox/get", Tree, Tree.Replace("],\"notFound\"", "," + added + "],\"notFound\"", StringComparison.Ordinal));

    private void AfterReplacing(string from, string to) => _jmap.OnEach("Mailbox/get", Tree, Tree.Replace(from, to, StringComparison.Ordinal));

    [Fact]
    public async Task Creates_a_top_level_folder_under_msgfolderroot()
    {
        After("""{"id":"h2","name":"Plans","parentId":null,"role":null,"totalEmails":0,"unreadEmails":0}""");
        _jmap.On("Mailbox/set", FakeJmapServer.Read("mailbox-set-created.json").Replace("\"h\"", "\"h2\"", StringComparison.Ordinal));

        var folder = await Writer().CreateAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Plans", Ct);

        folder.Value.ShouldBe(new MailFolder(new FolderId("h2"), "Plans", MailboxRoles.RootFolderId, 0, 0, 0));
        _jmap.Single("Mailbox/set")["create"]!["folder"]!.ToJsonString().ShouldBe("""{"name":"Plans"}""");
        _jmap.Requests().ShouldBe([["Mailbox/get"], ["Mailbox/set", "Mailbox/get"]]);
    }

    [Fact]
    public async Task Creates_a_child_of_a_folder_by_id_or_well_known_name()
    {
        _jmap.On("Mailbox/set", FakeJmapServer.Read("mailbox-set-created.json").Replace("\"h\"", "\"k\"", StringComparison.Ordinal));
        After("""{"id":"k","name":"Receipts","parentId":"a","role":null,"totalEmails":0,"unreadEmails":0}""");

        var folder = await Writer().CreateAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), "Receipts", Ct);

        folder.Value.ParentFolderId.ShouldBe(new FolderId("a"));
        _jmap.Single("Mailbox/set")["create"]!["folder"]!["parentId"]!.GetValue<string>().ShouldBe("a");
    }

    [Fact]
    public async Task A_null_parent_is_the_top_level()
    {
        _jmap.On("Mailbox/set", FakeJmapServer.Read("mailbox-set-created.json").Replace("\"h\"", "\"k\"", StringComparison.Ordinal));
        After("""{"id":"k","name":"Top","parentId":null,"role":null}""");

        (await Writer().CreateAsync(Alice, null, "Top", Ct)).Value.ParentFolderId.ShouldBe(MailboxRoles.RootFolderId);
    }

    [Fact]
    public async Task A_name_that_exists_is_409_ErrorFolderExists()
    {
        _jmap.OnFixture("Mailbox/set", "mailbox-set-not-created-exists.json");

        var result = await Writer().CreateAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Inbox", Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Code.ShouldBe("ErrorFolderExists");
    }

    [Fact]
    public async Task Other_create_failures_map_through_the_core()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s","notCreated":{"folder":{"type":"forbidden"}}}""");

        (await Writer().CreateAsync(Alice, null, "X", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Creating_under_an_unknown_parent_is_404_without_a_write()
    {
        (await Writer().CreateAsync(Alice, new FolderRef.ById(new FolderId("zz")), "X", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _jmap.Calls().ShouldNotContain(c => c.Method == "Mailbox/set");
    }

    [Fact]
    public async Task Rename_patches_the_name_and_returns_the_folder_with_its_counts()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s2","updated":{"h":null}}""");
        AfterReplacing("\"Projects\"", "\"Projects 2026\"");

        var folder = await Writer().RenameAsync(Alice, new FolderId("h"), "Projects 2026", Ct);

        folder.Value.ShouldBe(new MailFolder(new FolderId("h"), "Projects 2026", MailboxRoles.RootFolderId, 1, 1, 12));
        _jmap.Single("Mailbox/set")["update"]!.ToJsonString().ShouldBe("""{"h":{"name":"Projects 2026"}}""");
    }

    [Fact]
    public async Task Rename_to_a_sibling_name_is_409()
    {
        _jmap.On("Mailbox/set", FakeJmapServer.Read("mailbox-set-not-updated-exists.json").Replace("\"a\"", "\"h\"", StringComparison.Ordinal));

        (await Writer().RenameAsync(Alice, new FolderId("h"), "Inbox", Ct)).Error!.Code.ShouldBe("ErrorFolderExists");
    }

    [Theory]
    [InlineData("a", ErrorKind.Forbidden)]
    [InlineData("msgfolderroot", ErrorKind.Validation)]
    [InlineData("zz", ErrorKind.NotFound)]
    public async Task Well_known_folders_the_root_and_unknown_ids_cannot_be_renamed(string id, ErrorKind kind)
    {
        (await Writer().RenameAsync(Alice, new FolderId(id), "X", Ct)).Error!.Kind.ShouldBe(kind);

        _jmap.Calls().ShouldNotContain(c => c.Method == "Mailbox/set");
    }

    [Fact]
    public async Task Move_to_msgfolderroot_clears_the_parent()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s2","updated":{"i":null}}""");
        AfterReplacing("\"parentId\":\"h\"", "\"parentId\":null");

        var folder = await Writer().MoveAsync(Alice, new FolderId("i"), new FolderRef.ById(MailboxRoles.RootFolderId), Ct);

        folder.Value.ParentFolderId.ShouldBe(MailboxRoles.RootFolderId);
        folder.Value.ChildFolderCount.ShouldBe(1);
        _jmap.Single("Mailbox/set")["update"]!.ToJsonString().ShouldBe("""{"i":{"parentId":null}}""");
    }

    [Fact]
    public async Task Move_under_another_folder_sets_its_parent()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s2","updated":{"j":null}}""");

        (await Writer().MoveAsync(Alice, new FolderId("j"), new FolderRef.ById(new FolderId("h")), Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Mailbox/set")["update"]!.ToJsonString().ShouldBe("""{"j":{"parentId":"h"}}""");
    }

    [Fact]
    public async Task Move_into_its_own_subtree_is_refused_by_the_server_as_400()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s","notUpdated":{"h":{"type":"invalidProperties","description":"Mailbox cannot be a parent of itself.","properties":["parentId"]}}}""");

        (await Writer().MoveAsync(Alice, new FolderId("h"), new FolderRef.ById(new FolderId("j")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Move_to_an_unknown_destination_is_404()
    {
        (await Writer().MoveAsync(Alice, new FolderId("h"), new FolderRef.ById(new FolderId("zz")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Delete_destroys_the_subtree_deepest_first_with_its_mail()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s2","destroyed":["j","i","h"]}""");

        (await Writer().DeleteAsync(Alice, new FolderId("h"), Ct)).IsSuccess.ShouldBeTrue();

        var set = _jmap.Single("Mailbox/set");
        set["destroy"]!.ToJsonString().ShouldBe("""["j","i","h"]""");
        set["onDestroyRemoveEmails"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_reports_a_folder_the_server_kept()
    {
        _jmap.On("Mailbox/set", """{"accountId":"c","newState":"s2","destroyed":["j"],"notDestroyed":{"i":{"type":"mailboxHasChild"}}}""");

        (await Writer().DeleteAsync(Alice, new FolderId("i"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Theory]
    [InlineData("d", ErrorKind.Forbidden)]
    [InlineData("msgfolderroot", ErrorKind.Validation)]
    [InlineData("zz", ErrorKind.NotFound)]
    public async Task Well_known_folders_the_root_and_unknown_ids_cannot_be_deleted(string id, ErrorKind kind)
    {
        (await Writer().DeleteAsync(Alice, new FolderId(id), Ct)).Error!.Kind.ShouldBe(kind);

        _jmap.Calls().ShouldNotContain(c => c.Method == "Mailbox/set");
    }

    [Fact]
    public async Task A_subtree_containing_a_role_mailbox_is_not_deleted()
    {
        _jmap.On("Mailbox/get", Tree.Replace("{\"id\":\"j\",\"name\":\"Grandchild\",\"parentId\":\"i\",\"role\":null", "{\"id\":\"j\",\"name\":\"Archive\",\"parentId\":\"i\",\"role\":\"archive\"", StringComparison.Ordinal));

        (await Writer().DeleteAsync(Alice, new FolderId("h"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_failed_listing_is_returned()
    {
        _jmap.On("Mailbox/get", _ => [("error", JsonNode.Parse("""{"type":"accountNotFound"}""")!)]);

        (await Writer().DeleteAsync(Alice, new FolderId("h"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

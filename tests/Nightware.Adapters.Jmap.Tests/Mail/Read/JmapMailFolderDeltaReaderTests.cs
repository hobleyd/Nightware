using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailFolderDeltaReaderTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly DeltaRequest Request = new(100, null);
    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.On("Mailbox/get", """
            {"accountId":"c","state":"m7","list":[
              {"id":"p","name":"Projects","parentId":null,"role":null},
              {"id":"a","name":"Inbox","parentId":null,"role":"inbox","totalEmails":3,"unreadEmails":1},
              {"id":"q","name":"Q2","parentId":"p","role":null}],"notFound":[]}
            """);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailFolderDeltaReader Reader() => new(_jmap.Client());

    private static DeltaToken Since(string state) => JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes(JmapMailFolderDeltaReader.Hierarchy, state));

    [Fact]
    public async Task The_initial_sync_is_every_folder_parents_first_then_a_token_for_the_state_read()
    {
        var page = (await Reader().DeltaAsync(Alice, null, Request, Ct)).Value;

        page.Entries.Select(e => ((DeltaEntry<MailFolder>.Upsert)e).Item.DisplayName).ShouldBe(["Inbox", "Projects", "Q2"]);
        ((DeltaEntry<MailFolder>.Upsert)page.Entries[1]).Item.ChildFolderCount.ShouldBe(1);
        ((DeltaEntry<MailFolder>.Upsert)page.Entries[2]).Item.ParentFolderId.ShouldBe(new FolderId("p"));
        page.NextPage.ShouldBeNull();
        JmapDeltaTokenCodec.Decode(page.Final!.Value).Value.State.ShouldBe("m7");
    }

    [Fact]
    public async Task Changes_upsert_what_was_created_or_updated_and_remove_what_was_destroyed()
    {
        _jmap.On("Mailbox/changes", """{"accountId":"c","oldState":"m5","newState":"m7","hasMoreChanges":false,"created":["q"],"updated":["p"],"destroyed":["z"]}""");

        var page = (await Reader().DeltaAsync(Alice, Since("m5"), Request, Ct)).Value;

        page.Entries.OfType<DeltaEntry<MailFolder>.Upsert>().Select(u => u.Item.Id.Value).ShouldBe(["p", "q"]);
        page.Entries.OfType<DeltaEntry<MailFolder>.Removed>().ShouldHaveSingleItem().ShouldBe(new DeltaEntry<MailFolder>.Removed("z", RemovedReason.Deleted));
        JmapDeltaTokenCodec.Decode(page.Final!.Value).Value.State.ShouldBe("m7");
        _jmap.Single("Mailbox/changes")["sinceState"]!.GetValue<string>().ShouldBe("m5");
    }

    [Fact]
    public async Task More_changes_than_a_page_continue_with_a_next_page_token()
    {
        _jmap.On("Mailbox/changes", """{"accountId":"c","oldState":"m5","newState":"m6","hasMoreChanges":true,"created":[],"updated":["a"],"destroyed":[]}""");

        var page = (await Reader().DeltaAsync(Alice, Since("m5"), Request, Ct)).Value;

        page.Final.ShouldBeNull();
        JmapDeltaTokenCodec.Decode(page.NextPage!.Value).Value.State.ShouldBe("m6");
    }

    [Fact]
    public async Task A_state_too_old_is_410_and_a_token_for_a_message_folder_is_rejected()
    {
        _jmap.On("Mailbox/changes", _ => [("error", System.Text.Json.Nodes.JsonNode.Parse("""{"type":"cannotCalculateChanges"}""")!)]);

        (await Reader().DeltaAsync(Alice, Since("m1"), Request, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        var messageToken = JmapDeltaTokenCodec.Encode(JmapDeltaCursor.Changes("a", "s1"));
        (await Reader().DeltaAsync(Alice, messageToken, Request, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }
}

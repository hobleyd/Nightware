using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapMailMessageWriterTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly MessageId Id = new("e.qaaaaaf");
    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.Locate(Id.Value, "qaaaaaf");
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailMessageWriter Writer() => new(_jmap.Client(), _jmap.Locator);

    private void StubEmail(string mailboxIds, string keywords = """{"$seen":true}""", string messageId = """["m1@example.com"]""") =>
        _jmap.On("Email/get", $$"""{"accountId":"c","state":"s1","list":[{"id":"qaaaaaf","keywords":{{keywords}},"messageId":{{messageId}},"mailboxIds":{{mailboxIds}}}],"notFound":[]}""");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Patching_isRead_sets_or_clears_seen(bool isRead)
    {
        _jmap.OnFixture("Email/set", "email-set-updated.json");

        var result = await Writer().PatchAsync(Alice, Id, new MessagePatch { IsRead = isRead }, Ct);

        result.IsSuccess.ShouldBeTrue();
        var update = _jmap.Single("Email/set")["update"]!["qaaaaaf"]!.AsObject();
        update.ToJsonString().ShouldBe(isRead ? """{"keywords/$seen":true}""" : """{"keywords/$seen":null}""");
    }

    [Fact]
    public async Task A_flag_maps_to_the_flagged_keyword()
    {
        _jmap.OnFixture("Email/set", "email-set-updated.json");

        (await Writer().PatchAsync(Alice, Id, new MessagePatch { Flag = new FollowUpFlag(FlagStatus.Flagged) }, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["update"]!["qaaaaaf"]!.ToJsonString().ShouldBe("""{"keywords/$flagged":true}""");
    }

    [Theory]
    [MemberData(nameof(UnsupportedPatches))]
    public async Task What_jmap_cannot_store_on_a_message_is_501_before_any_request(MessagePatch patch)
    {
        var result = await Writer().PatchAsync(Alice, Id, patch, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _jmap.Calls().ShouldBeEmpty();
    }

    public static TheoryData<MessagePatch> UnsupportedPatches() =>
    [
        new MessagePatch { Flag = new FollowUpFlag(FlagStatus.Complete) },
        new MessagePatch { Categories = ["Red"] },
        new MessagePatch { Importance = Importance.High },
    ];

    [Fact]
    public async Task An_empty_patch_resolves_the_message_and_writes_nothing()
    {
        (await Writer().PatchAsync(Alice, Id, new MessagePatch(), Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Calls().ShouldBeEmpty();
        await _jmap.Locator.Received(1).ResolveAsync(Arg.Any<JmapAccount>(), Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_the_locator_cannot_find_is_404()
    {
        _jmap.Locator.ResolveAsync(Arg.Any<JmapAccount>(), new MessageId("e.gone"), Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure(Error.NotFound("gone")));

        var result = await Writer().PatchAsync(Alice, new MessageId("e.gone"), new MessagePatch { IsRead = true }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _jmap.Calls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_update_maps_the_set_error()
    {
        _jmap.On("Email/set", """{"accountId":"c","newState":"s2","notUpdated":{"qaaaaaf":{"type":"notFound"}}}""");

        var result = await Writer().PatchAsync(Alice, Id, new MessagePatch { IsRead = true }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Move_replaces_the_mailboxes_and_keeps_the_id()
    {
        StubEmail("""{"a":true}""");
        _jmap.OnFixture("Email/set", "email-set-updated.json");

        var moved = await Writer().MoveAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.JunkEmail), Ct);

        moved.Value.ShouldBe(Id);
        _jmap.Single("Email/set")["update"]!["qaaaaaf"]!.ToJsonString().ShouldBe("""{"mailboxIds":{"c":true}}""");
        _jmap.Requests().ShouldBe([["Mailbox/get", "Email/get"], ["Email/set"]]);
    }

    [Fact]
    public async Task Moving_a_draft_keeps_its_draft_id()
    {
        StubEmail("""{"d":true}""", """{"$draft":true}""");
        _jmap.OnFixture("Email/set", "email-set-updated.json");

        var moved = await Writer().MoveAsync(Alice, Id, new FolderRef.ById(new FolderId("a")), Ct);

        moved.Value.Value.ShouldBe("d.bTFAZXhhbXBsZS5jb20");
    }

    [Fact]
    public async Task Move_to_an_unknown_folder_is_404_and_to_the_root_is_400()
    {
        StubEmail("""{"a":true}""");

        (await Writer().MoveAsync(Alice, Id, new FolderRef.ById(new FolderId("zz")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Writer().MoveAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task Move_of_a_message_the_server_no_longer_has_is_404()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"s1","list":[],"notFound":["qaaaaaf"]}""");

        (await Writer().MoveAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Delete_moves_to_deleted_items()
    {
        StubEmail("""{"a":true}""");
        _jmap.OnFixture("Email/set", "email-set-updated.json");

        (await Writer().SoftDeleteAsync(Alice, Id, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["update"]!["qaaaaaf"]!.ToJsonString().ShouldBe("""{"mailboxIds":{"b":true}}""");
    }

    [Fact]
    public async Task Delete_from_deleted_items_destroys()
    {
        StubEmail("""{"b":true}""");
        _jmap.On("Email/set", """{"accountId":"c","newState":"s2","destroyed":["qaaaaaf"]}""");

        (await Writer().SoftDeleteAsync(Alice, Id, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["destroy"]!.ToJsonString().ShouldBe("""["qaaaaaf"]""");
    }

    [Fact]
    public async Task Delete_without_a_deleted_items_folder_is_404()
    {
        _jmap.On("Mailbox/get", """{"accountId":"c","state":"s","list":[{"id":"a","name":"Inbox","role":"inbox"}],"notFound":[]}""");
        StubEmail("""{"a":true}""");

        (await Writer().SoftDeleteAsync(Alice, Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Permanent_delete_destroys()
    {
        _jmap.On("Email/set", """{"accountId":"c","newState":"s2","destroyed":["qaaaaaf"]}""");

        (await Writer().PermanentDeleteAsync(Alice, Id, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Calls().ShouldHaveSingleItem().Arguments["destroy"]!.ToJsonString().ShouldBe("""["qaaaaaf"]""");
    }

    [Fact]
    public async Task Permanent_delete_of_a_missing_message_maps_not_destroyed()
    {
        _jmap.On("Email/set", FakeJmapServer.Read("email-set-not-destroyed.json").Replace("zzzzzz", "qaaaaaf", StringComparison.Ordinal));

        (await Writer().PermanentDeleteAsync(Alice, Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_method_error_is_mapped()
    {
        _jmap.On("Email/set", _ => [("error", JsonNode.Parse("""{"type":"forbidden"}""")!)]);

        (await Writer().PermanentDeleteAsync(Alice, Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

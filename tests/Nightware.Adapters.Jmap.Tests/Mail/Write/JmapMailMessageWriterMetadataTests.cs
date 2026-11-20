using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

/// <summary>Flag, categories, importance and copy on a server that keeps message metadata (ADR 0006 D5).</summary>
public sealed class JmapMailMessageWriterMetadataTests : IAsyncLifetime
{
    private const string Metadata = "urn:ietf:params:jmap:metadata";
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly MessageId Id = new("e.qaaaaaf");
    private readonly FakeJmapServer _jmap = new(JmapTestHost.SessionWithMetadata());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.Locate(Id.Value, "qaaaaaf");
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.OnFixture("Email/set", "email-set-updated.json");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailMessageWriter Writer() => new(_jmap.Client(), _jmap.Locator);

    private void StoredMetadata(string json) =>
        _jmap.On("Email/get", $$"""{"accountId":"c","state":"s1","list":[{"id":"qaaaaaf","metadata":{{json}}}],"notFound":[]}""");

    private JsonObject WrittenUpdate() => _jmap.Single("Email/set")["update"]!["qaaaaaf"]!.AsObject();

    private bool EveryRequestDeclaresMetadata() =>
        _jmap.Host.ApiBodies().All(b => b["using"]!.AsArray().Any(u => u!.GetValue<string>() == Metadata));

    [Fact]
    public async Task A_completed_flag_clears_flagged_and_keeps_the_rest_of_the_namespace()
    {
        StoredMetadata("""{"nightware.ms":{"categories":["Red"]}}""");
        var completed = new DateTimeTimeZone(new DateTime(2026, 11, 21, 9, 30, 0), "UTC");

        var result = await Writer().PatchAsync(Alice, Id, new MessagePatch { Flag = new FollowUpFlag(FlagStatus.Complete) { CompletedDateTime = completed } }, Ct);

        result.IsSuccess.ShouldBeTrue();
        WrittenUpdate().ToJsonString().ShouldBe(
            """{"keywords/$flagged":null,"metadata/nightware.ms":{"flag":{"status":"complete","completed":{"dateTime":"2026-11-21T09:30:00.0000000","timeZone":"UTC"}},"categories":["Red"]}}""");
        EveryRequestDeclaresMetadata().ShouldBeTrue();
    }

    [Fact]
    public async Task Categories_and_importance_are_written_whole_into_the_namespace()
    {
        StoredMetadata("{}");

        (await Writer().PatchAsync(Alice, Id, new MessagePatch { Categories = ["Blue category", "Ünïcode"], Importance = Importance.High }, Ct))
            .IsSuccess.ShouldBeTrue();

        JsonNode.DeepEquals(WrittenUpdate(), JsonNode.Parse("""{"metadata/nightware.ms":{"categories":["Blue category","Ünïcode"],"importance":"high"}}""")).ShouldBeTrue();
    }

    [Fact]
    public async Task Clearing_everything_removes_the_namespace()
    {
        StoredMetadata("""{"nightware.ms":{"flag":{"status":"complete"},"categories":["Red"],"importance":"low"}}""");

        var patch = new MessagePatch { Flag = FollowUpFlag.NotFlagged, Categories = [], Importance = Importance.Normal };
        (await Writer().PatchAsync(Alice, Id, patch, Ct)).IsSuccess.ShouldBeTrue();

        WrittenUpdate().ToJsonString().ShouldBe("""{"keywords/$flagged":null,"metadata/nightware.ms":null}""");
    }

    [Fact]
    public async Task A_plain_flag_toggle_with_nothing_stored_writes_only_the_keyword()
    {
        StoredMetadata("""{"nightware.ms":{"categories":["Red"]}}""");

        (await Writer().PatchAsync(Alice, Id, new MessagePatch { Flag = new FollowUpFlag(FlagStatus.Flagged) }, Ct)).IsSuccess.ShouldBeTrue();

        WrittenUpdate().ToJsonString().ShouldBe("""{"keywords/$flagged":true}""");
    }

    [Fact]
    public async Task Unflagging_a_completed_message_clears_the_stored_flag()
    {
        StoredMetadata("""{"nightware.ms":{"flag":{"status":"complete"}}}""");

        (await Writer().PatchAsync(Alice, Id, new MessagePatch { Flag = FollowUpFlag.NotFlagged }, Ct)).IsSuccess.ShouldBeTrue();

        WrittenUpdate().ToJsonString().ShouldBe("""{"keywords/$flagged":null,"metadata/nightware.ms":null}""");
    }

    [Fact]
    public async Task Read_state_alone_writes_only_the_keyword()
    {
        (await Writer().PatchAsync(Alice, Id, new MessagePatch { IsRead = true }, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Requests().ShouldBe([["Email/set"]]);
        WrittenUpdate().ToJsonString().ShouldBe("""{"keywords/$seen":true}""");
    }

    [Fact]
    public async Task Categories_over_the_namespace_limit_are_refused_without_writing()
    {
        StoredMetadata("{}");
        var categories = Enumerable.Range(0, 400).Select(static i => $"A rather long category name number {i}").ToList();

        var result = await Writer().PatchAsync(Alice, Id, new MessagePatch { Categories = categories }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task Copy_imports_the_blob_into_the_target_and_carries_the_metadata()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"s1","list":[{"id":"qaaaaaf","blobId":"B1","keywords":{"$seen":true,"$flagged":true},"messageId":["m1@example.com"],"receivedAt":"2026-11-20T22:31:44Z","metadata":{"nightware.ms":{"categories":["Red"],"importance":"high"}}}],"notFound":[]}""");
        _jmap.On("Email/import", """{"accountId":"c","oldState":"s1","newState":"s2","created":{"copy":{"id":"qaaaaag","blobId":"B1","threadId":"t","size":10}}}""");
        _jmap.On("Email/set", """{"accountId":"c","oldState":"s2","newState":"s3","updated":{"qaaaaag":null}}""");

        var copied = await Writer().CopyAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct);

        copied.Value.ShouldBe(new MessageId("e.qaaaaag"));
        var import = _jmap.Single("Email/import")["emails"]!["copy"]!;
        import["blobId"]!.GetValue<string>().ShouldBe("B1");
        import["keywords"]!.ToJsonString().ShouldBe("""{"$seen":true,"$flagged":true}""");
        import["receivedAt"]!.GetValue<string>().ShouldBe("2026-11-20T22:31:44Z");
        import["mailboxIds"]!.AsObject().Count.ShouldBe(1);
        _jmap.Single("Email/set")["update"]!["qaaaaag"]!.ToJsonString()
            .ShouldBe("""{"metadata/nightware.ms":{"categories":["Red"],"importance":"high"}}""");
    }

    [Fact]
    public async Task A_copy_without_metadata_is_one_import()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"s1","list":[{"id":"qaaaaaf","blobId":"B1","keywords":{},"messageId":["m1@example.com"],"receivedAt":"2026-11-20T22:31:44Z","metadata":{}}],"notFound":[]}""");
        _jmap.On("Email/import", """{"accountId":"c","oldState":"s1","newState":"s2","created":{"copy":{"id":"qaaaaag"}}}""");

        (await Writer().CopyAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task Copy_to_the_root_is_400_and_to_an_unknown_folder_404()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"s1","list":[{"id":"qaaaaaf","blobId":"B1","keywords":{}}],"notFound":[]}""");

        (await Writer().CopyAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Writer().CopyAsync(Alice, Id, new FolderRef.ById(new FolderId("nope")), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/import");
    }

    [Fact]
    public async Task Copy_of_a_message_the_server_no_longer_has_is_404()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"s1","list":[],"notFound":["qaaaaaf"]}""");

        (await Writer().CopyAsync(Alice, Id, new FolderRef.WellKnown(WellKnownFolder.Inbox), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

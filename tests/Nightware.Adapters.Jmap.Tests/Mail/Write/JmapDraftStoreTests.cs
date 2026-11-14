using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

/// <summary>Fixtures in <c>Fixtures/Mail/Write</c> are Stalwart v1.1 responses to these request shapes.</summary>
public sealed class JmapDraftStoreTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");

    /// <summary>The recorded draft (email-get-draft.json): HTML body, an inline image (part 3) and a file (part 4).</summary>
    private const string DraftJmapId = "bqaaaaam";
    private const string DraftMessageId = "rec-1794695759@nightware.localhost";
    private static readonly MessageId DraftId = MailItemId.ForDraft(DraftMessageId).ToMessageId();

    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.OnFixture("Email/set", "email-set-created.json", when: static a => a.ContainsKey("create"));
        _jmap.On("Email/set", $$"""{"accountId":"c","newState":"s9","destroyed":["{{DraftJmapId}}"]}""", when: static a => a.ContainsKey("destroy"));
        _jmap.OnFixture("Email/get", "email-get-draft.json");
        _jmap.Locate(DraftId.Value, DraftJmapId);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapDraftStore Store() => new(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User);

    private JsonObject Created() => _jmap.Calls().Single(c => c.Method == "Email/set" && c.Arguments.ContainsKey("create")).Arguments["create"]!["draft"]!.AsObject();

    private static DraftContent Content => new()
    {
        Subject = "Draft: Q2 follow-up",
        Body = new ItemBody(BodyType.Html, "<p>Draft text</p>"),
        ToRecipients = [new EmailAddress("bob@nightware.localhost")],
        CcRecipients = [new EmailAddress("carol@example.com", "Carol")],
    };

    [Fact]
    public async Task Create_saves_a_draft_in_drafts_with_a_minted_message_id_and_returns_its_draft_id()
    {
        var id = await Store().CreateAsync(Alice, Content, Ct);

        var email = Created();
        var messageId = email["messageId"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>();
        messageId.ShouldEndWith("@nightware.localhost");
        id.Value.ShouldBe(MailItemId.ForDraft(messageId).ToMessageId());
        email["mailboxIds"]!.ToJsonString().ShouldBe("""{"d":true}""");
        email["keywords"]!.ToJsonString().ShouldBe("""{"$draft":true,"$seen":true}""");
        email["from"]!.ToJsonString().ShouldBe("""[{"name":"Alice","email":"alice@nightware.localhost"}]""");
        email["to"]!.ToJsonString().ShouldBe("""[{"email":"bob@nightware.localhost"}]""");
        email["cc"]!.ToJsonString().ShouldBe("""[{"name":"Carol","email":"carol@example.com"}]""");
        email.ContainsKey("bcc").ShouldBeFalse();
        email["subject"]!.GetValue<string>().ShouldBe("Draft: Q2 follow-up");
        email["htmlBody"]!.ToJsonString().ShouldBe("""[{"partId":"body","type":"text/html"}]""");
        email.ContainsKey("textBody").ShouldBeFalse();
        email["bodyValues"]!["body"]!["value"]!.GetValue<string>().ShouldBe("<p>Draft text</p>");
        _jmap.Requests().ShouldBe([["Mailbox/get"], ["Email/set"]]);
    }

    [Fact]
    public async Task Create_uploads_new_attachments_and_references_their_blobs()
    {
        _jmap.StubUpload("Gnotes", "text/plain", 14);
        var content = Content with
        {
            Body = new ItemBody(BodyType.Text, "see attached"),
            Attachments = [new NewAttachment("notes.txt", "text/plain; charset=utf-8", "Meeting notes\n"u8.ToArray()) { IsInline = true, ContentId = "<n1@x>" }],
            Importance = Importance.High,
        };

        (await Store().CreateAsync(Alice, content, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.UploadedBody().ShouldBe("Meeting notes\n"u8.ToArray());
        var email = Created();
        email["textBody"]!.ToJsonString().ShouldBe("""[{"partId":"body","type":"text/plain"}]""");
        email["attachments"]!.ToJsonString().ShouldBe("""[{"blobId":"Gnotes","name":"notes.txt","type":"text/plain","disposition":"inline","cid":"n1@x"}]""");
        email["header:Importance:asText"]!.GetValue<string>().ShouldBe("high");
    }

    [Fact]
    public async Task Create_for_a_forward_carries_the_original_attachments_by_blob()
    {
        var original = new MessageId("e.orig");
        _jmap.Locate(original.Value, "orig");
        var content = Content with { AttachmentsFrom = original, InReplyTo = null, References = ["root@x", "<orig@x>"] };

        (await Store().CreateAsync(Alice, content, Ct)).IsSuccess.ShouldBeTrue();

        var get = _jmap.Single("Email/get");
        get["ids"]!.ToJsonString().ShouldBe("""["orig"]""");
        get["properties"]!.ToJsonString().ShouldBe("""["id","textBody","htmlBody","attachments"]""");
        var email = Created();
        email["attachments"]!.ToJsonString().ShouldBe(
            """[{"blobId":"cmwdz0t3bjmyvkh3zjnen3jr1ikivvczslnk9eohpb9rkuvlqsgjaaqabtfqofq","name":"img.png","type":"image/png","disposition":"inline","cid":"img1@x"},"""
            + """{"blobId":"ciwdz0t3bjmyvkh3zjnen3jr1ikivvczslnk9eohpb9rkuvlqsgjaaqabtjqsea","name":"notes.txt","type":"text/plain","charset":"us-ascii","disposition":"attachment"}]""");
        email["references"]!.ToJsonString().ShouldBe("""["root@x","orig@x"]""");
        email.ContainsKey("inReplyTo").ShouldBeFalse();
    }

    [Fact]
    public async Task Create_without_a_drafts_mailbox_is_404()
    {
        _jmap.On("Mailbox/get", """{"accountId":"c","state":"s","list":[{"id":"a","name":"Inbox","role":"inbox"}],"notFound":[]}""");

        (await Store().CreateAsync(Alice, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task A_rejected_create_maps_the_set_error()
    {
        _jmap.OnFixture("Email/set", "email-set-not-created.json");

        (await Store().CreateAsync(Alice, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Invalid_header_names_are_400_before_any_write()
    {
        var content = Content with { ExtraHeaders = [new InternetHeader("X Bad:", "v")] };

        (await Store().CreateAsync(Alice, content, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task Replace_creates_the_new_draft_then_destroys_the_old_and_keeps_the_id()
    {
        var replaced = await Store().ReplaceAsync(Alice, DraftId, Content with { Body = new ItemBody(BodyType.Text, "Updated") }, Ct);

        replaced.Value.ShouldBe(DraftId);
        _jmap.Requests().ShouldBe([["Email/get"], ["Email/set"], ["Email/set"]]);
        var email = Created();
        email["messageId"]!.ToJsonString().ShouldBe($"""["{DraftMessageId}"]""");
        email["mailboxIds"]!.ToJsonString().ShouldBe("""{"d":true}""");
        email["keywords"]!.ToJsonString().ShouldBe("""{"$draft":true,"$seen":true}""", "the server-derived $hasattachment is not carried");
        email["from"]!.ToJsonString().ShouldBe("""[{"name":"Alice Anderson","email":"alice@nightware.localhost"}]""");
        email["inReplyTo"]!.ToJsonString().ShouldBe("""["original-0001@example.com"]""", "a reply draft keeps threading");
        email["references"]!.ToJsonString().ShouldBe("""["root-0001@example.com","original-0001@example.com"]""");
        email["textBody"]!.ToJsonString().ShouldBe("""[{"partId":"body","type":"text/plain"}]""");
        email["attachments"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ShouldBe(["img.png", "notes.txt"]);
        email["attachments"]!.AsArray().ShouldAllBe(a => !a!.AsObject().ContainsKey("partId") && !a.AsObject().ContainsKey("size"));
        _jmap.Calls()[^1].Arguments["destroy"]!.ToJsonString().ShouldBe($"""["{DraftJmapId}"]""");
    }

    [Fact]
    public async Task Replace_reads_the_draft_with_its_body_values_and_parts()
    {
        (await Store().ReplaceAsync(Alice, DraftId, Content, Ct)).IsSuccess.ShouldBeTrue();

        var get = _jmap.Single("Email/get");
        get["ids"]!.ToJsonString().ShouldBe($"""["{DraftJmapId}"]""");
        get["fetchTextBodyValues"]!.GetValue<bool>().ShouldBeTrue();
        get["fetchHTMLBodyValues"]!.GetValue<bool>().ShouldBeTrue();
        get["bodyProperties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldContain("blobId");
    }

    [Fact]
    public async Task A_failed_create_leaves_the_old_draft_alone()
    {
        _jmap.OnFixture("Email/set", "email-set-not-created.json", when: static a => a.ContainsKey("create"));

        (await Store().ReplaceAsync(Alice, DraftId, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Arguments.ContainsKey("destroy"));
    }

    [Fact]
    public async Task A_failed_destroy_is_reported()
    {
        _jmap.On("Email/set", """{"accountId":"c","newState":"s9","notDestroyed":{"bqaaaaam":{"type":"forbidden"}}}""", when: static a => a.ContainsKey("destroy"));

        (await Store().ReplaceAsync(Alice, DraftId, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Only_drafts_can_be_replaced()
    {
        _jmap.On("Email/get", FakeJmapServer.Read("email-get-draft.json").Replace("\"$draft\": true,", string.Empty, StringComparison.Ordinal));

        (await Store().ReplaceAsync(Alice, DraftId, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task A_draft_without_a_message_id_gets_one_and_a_draft_id()
    {
        _jmap.Locate("e.bqaaaaam", DraftJmapId);
        _jmap.On("Email/get", FakeJmapServer.Read("email-get-draft.json").Replace($"\"messageId\": [\n        \"{DraftMessageId}\"\n      ]", "\"messageId\": null", StringComparison.Ordinal));

        var replaced = await Store().ReplaceAsync(Alice, new MessageId("e.bqaaaaam"), Content, Ct);

        replaced.Value.Value.ShouldStartWith(MailItemId.DraftPrefix);
        replaced.Value.ShouldNotBe(DraftId);
    }

    [Fact]
    public async Task A_draft_the_locator_cannot_find_is_404()
    {
        var missing = MailItemId.ForDraft("gone@x").ToMessageId();
        _jmap.Locator.ResolveAsync(Arg.Any<JmapAccount>(), missing, Arg.Any<CancellationToken>()).Returns(Result<string>.Failure(Error.NotFound("gone")));

        (await Store().ReplaceAsync(Alice, missing, Content, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _jmap.Calls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_an_attachment_rebuilds_the_draft_unchanged_plus_the_file()
    {
        _jmap.StubUpload("Gq2", "text/csv", 16);
        _jmap.On("Email/get", """
            {"accountId":"c","state":"s9","list":[{"id":"newid","attachments":[
              {"partId":"3","blobId":"x3","size":15,"name":"img.png","type":"image/png","disposition":"inline","cid":"img1@x"},
              {"partId":"4","blobId":"x4","size":14,"name":"notes.txt","type":"text/plain","disposition":"attachment"},
              {"partId":"5","blobId":"x5","size":16,"name":"q2.csv","type":"text/csv","disposition":"attachment"}]}],"notFound":[]}
            """, when: static a => a["ids"]![0]!.GetValue<string>() == "#draft");

        var added = await Store().AddAttachmentAsync(Alice, DraftId, new NewAttachment("q2.csv", "text/csv", "q,revenue\n2,100\n"u8.ToArray()), Ct);

        added.Value.ShouldBe(new MailAttachment(new AttachmentId("5"), "q2.csv", "text/csv", 16, AttachmentKind.File));
        _jmap.Requests().ShouldBe([["Email/get"], ["Email/set", "Email/get"], ["Email/set"]]);
        var email = Created();
        email["htmlBody"]!.ToJsonString().ShouldBe("""[{"partId":"body","type":"text/html"}]""");
        email["bodyValues"]!["body"]!["value"]!.GetValue<string>().ShouldBe("""<p>see <img src="cid:img1@x"></p>""");
        email["subject"]!.GetValue<string>().ShouldBe("RE: Quarterly numbers");
        email["to"]!.ToJsonString().ShouldBe("""[{"email":"bob@nightware.localhost"}]""");
        email["header:Importance:asText"]!.GetValue<string>().ShouldBe("high");
        email["attachments"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ShouldBe(["img.png", "notes.txt", "q2.csv"]);
        email["attachments"]![2]!["blobId"]!.GetValue<string>().ShouldBe("Gq2");
    }

    [Fact]
    public async Task Removing_an_attachment_rebuilds_the_draft_without_that_part()
    {
        (await Store().RemoveAttachmentAsync(Alice, DraftId, new AttachmentId("4"), Ct)).IsSuccess.ShouldBeTrue();

        Created()["attachments"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("img.png");
        _jmap.Calls()[^1].Arguments["destroy"]!.ToJsonString().ShouldBe($"""["{DraftJmapId}"]""");
    }

    [Fact]
    public async Task Removing_an_unknown_attachment_is_404_without_a_write()
    {
        (await Store().RemoveAttachmentAsync(Alice, DraftId, new AttachmentId("9"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }

    [Fact]
    public async Task A_failed_upload_writes_nothing()
    {
        _jmap.Host.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/jmap/upload/c/").UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(413));

        var added = await Store().AddAttachmentAsync(Alice, DraftId, new NewAttachment("big.bin", "application/octet-stream", new byte[3]), Ct);

        added.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set");
    }
}

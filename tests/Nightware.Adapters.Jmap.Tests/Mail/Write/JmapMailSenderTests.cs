using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapMailSenderTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly MessageId DraftId = new("d.ZHJhZnRAeA");
    private const string DraftJmapId = "qaaaaae";

    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.Locate(DraftId.Value, DraftJmapId);
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.OnFixture("Identity/get", "identity-get.json");
        _jmap.On("Email/set", Created("email-set-created.json"), when: static a => a.ContainsKey("create"));
        StubDraft("""{"$draft":true,"$seen":true}""", """[{"name":"Alice Anderson","email":"alice@nightware.localhost"}]""");
        Submission(FakeJmapServer.Read("email-set-implicit-updated.json"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailSender Sender() => new(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User, NullLogger<JmapMailSender>.Instance);

    /// <summary>A recorded Email/set response with its creation id renamed to the sender's.</summary>
    private static string Created(string fixture) => FakeJmapServer.Read(fixture).Replace("\"draft\"", "\"message\"", StringComparison.Ordinal);

    private void StubDraft(string keywords, string from) =>
        _jmap.On("Email/get", $$$"""{"accountId":"c","state":"s","list":[{"id":"{{{DraftJmapId}}}","from":{{{from}}},"keywords":{{{keywords}}},"mailboxIds":{"d":true}}],"notFound":[]}""");

    /// <summary>The submission's response and the implicit Email/set that onSuccess* produces.</summary>
    private void Submission(string implicitEmailSet, string? submission = null) =>
        _jmap.On("EmailSubmission/set", _ =>
        [
            ("EmailSubmission/set", JsonNode.Parse(submission ?? FakeJmapServer.Read("email-submission-set-created.json"))!),
            ("Email/set", JsonNode.Parse(implicitEmailSet)!),
        ]);

    private static DraftContent Content => new()
    {
        Subject = "Q2 numbers",
        Body = new ItemBody(BodyType.Html, "<p>See attached.</p>"),
        ToRecipients = [new EmailAddress("bob@nightware.localhost")],
        BccRecipients = [new EmailAddress("carol@example.com")],
    };

    [Fact]
    public async Task Sending_a_draft_submits_it_and_files_it_in_sent_items()
    {
        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Requests().ShouldBe([["Email/get", "Mailbox/get", "Identity/get"], ["EmailSubmission/set"]]);
        var submit = _jmap.Single("EmailSubmission/set");
        submit["create"]!.ToJsonString().ShouldBe($$$"""{"send":{"identityId":"b","emailId":"{{{DraftJmapId}}}"}}""");
        submit["onSuccessUpdateEmail"]!.ToJsonString().ShouldBe("""{"#send":{"keywords/$draft":null,"keywords/$seen":true,"mailboxIds":{"e":true}}}""");
        submit.ContainsKey("onSuccessDestroyEmail").ShouldBeFalse();
    }

    [Fact]
    public async Task Only_drafts_are_sent()
    {
        StubDraft("""{"$seen":true}""", """[{"email":"alice@nightware.localhost"}]""");

        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "EmailSubmission/set");
    }

    [Fact]
    public async Task A_draft_from_someone_the_mailbox_cannot_send_as_is_403()
    {
        StubDraft("""{"$draft":true}""", """[{"email":"bob@nightware.localhost"}]""");

        var result = await Sender().SendDraftAsync(Alice, DraftId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        result.Error.Code.ShouldBe("ErrorSendAsDenied");
        _jmap.Calls().ShouldNotContain(c => c.Method == "EmailSubmission/set" || c.Method == "Identity/set");
    }

    [Fact]
    public async Task An_account_without_an_identity_gets_one_for_its_own_address()
    {
        _jmap.On("Identity/get", """{"accountId":"c","state":"s","list":[],"notFound":[]}""");
        _jmap.OnFixture("Identity/set", "identity-set-created.json");

        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Identity/set")["create"]!.ToJsonString().ShouldBe("""{"identity":{"name":"Alice Anderson","email":"alice@nightware.localhost"}}""");
        _jmap.Single("EmailSubmission/set")["create"]!["send"]!["identityId"]!.GetValue<string>().ShouldBe("c");
    }

    [Fact]
    public async Task A_wildcard_identity_sends_for_its_domain()
    {
        _jmap.On("Identity/get", """{"accountId":"c","state":"s","list":[{"id":"w","name":"","email":"*@nightware.localhost"}],"notFound":[]}""");

        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("EmailSubmission/set")["create"]!["send"]!["identityId"]!.GetValue<string>().ShouldBe("w");
    }

    [Fact]
    public async Task A_refused_submission_is_returned()
    {
        Submission("""{"accountId":"c","newState":"s"}""", """{"accountId":"c","newState":"s","notCreated":{"send":{"type":"forbiddenToSend","description":"quota"}}}""");

        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_sent_message_that_cannot_be_filed_is_still_sent()
    {
        Submission("""{"accountId":"c","newState":"s","notUpdated":{"qaaaaae":{"type":"notFound"}}}""");

        (await Sender().SendDraftAsync(Alice, DraftId, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SendMail_stores_the_message_as_a_draft_and_submits_it_in_one_request()
    {
        (await Sender().SendAsync(Alice, Content, saveToSentItems: true, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Requests().ShouldBe([["Mailbox/get", "Identity/get"], ["Email/set", "EmailSubmission/set"]]);
        var email = _jmap.Single("Email/set")["create"]!["message"]!.AsObject();
        email["mailboxIds"]!.ToJsonString().ShouldBe("""{"d":true}""");
        email["keywords"]!.ToJsonString().ShouldBe("""{"$draft":true,"$seen":true}""");
        email["from"]!.ToJsonString().ShouldBe("""[{"name":"Alice","email":"alice@nightware.localhost"}]""");
        email["bcc"]!.ToJsonString().ShouldBe("""[{"email":"carol@example.com"}]""", "the server derives the envelope from the headers and strips Bcc on delivery");
        var submit = _jmap.Single("EmailSubmission/set");
        submit["create"]!["send"]!["emailId"]!.GetValue<string>().ShouldBe("#message");
        submit["create"]!["send"]!.AsObject().ContainsKey("envelope").ShouldBeFalse();
        submit["onSuccessUpdateEmail"]!["#send"]!["mailboxIds"]!.ToJsonString().ShouldBe("""{"e":true}""");
    }

    [Fact]
    public async Task SendMail_without_saving_destroys_the_message_once_submitted()
    {
        Submission("""{"accountId":"c","newState":"s","destroyed":["x"]}""");

        (await Sender().SendAsync(Alice, Content, saveToSentItems: false, Ct)).IsSuccess.ShouldBeTrue();

        var submit = _jmap.Single("EmailSubmission/set");
        submit["onSuccessDestroyEmail"]!.ToJsonString().ShouldBe("""["#send"]""");
        submit.ContainsKey("onSuccessUpdateEmail").ShouldBeFalse();
    }

    [Fact]
    public async Task SendMail_uploads_attachments_first()
    {
        _jmap.StubUpload("Gq2", "text/csv", 16);

        (await Sender().SendAsync(Alice, Content with { Attachments = [new NewAttachment("q2.csv", "text/csv", "q,revenue\n2,100\n"u8.ToArray())] }, true, Ct))
            .IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["create"]!["message"]!["attachments"]!.ToJsonString()
            .ShouldBe("""[{"blobId":"Gq2","name":"q2.csv","type":"text/csv","disposition":"attachment"}]""");
    }

    [Fact]
    public async Task A_forward_sent_at_once_carries_the_original_attachments()
    {
        _jmap.Locate("e.orig", "orig");
        _jmap.OnFixture("Email/get", "email-get-draft.json");

        (await Sender().SendAsync(Alice, Content with { AttachmentsFrom = new MessageId("e.orig") }, true, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["create"]!["message"]!["attachments"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_submission_removes_the_stored_message()
    {
        Submission("""{"accountId":"c","newState":"s"}""", """{"accountId":"c","newState":"s","notCreated":{"send":{"type":"invalidRecipients","invalidRecipients":["x"]}}}""");
        _jmap.On("Email/set", """{"accountId":"c","newState":"s","destroyed":["bqaaaaam"]}""", when: static a => a.ContainsKey("destroy"));

        (await Sender().SendAsync(Alice, Content, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        _jmap.Calls()[^1].Arguments["destroy"]!.ToJsonString().ShouldBe("""["bqaaaaam"]""");
    }

    [Fact]
    public async Task A_message_that_cannot_be_stored_is_not_submitted()
    {
        _jmap.On("Email/set", Created("email-set-not-created.json"), when: static a => a.ContainsKey("create"));

        (await Sender().SendAsync(Alice, Content, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Arguments.ContainsKey("destroy"));
    }

    [Fact]
    public async Task SendMail_from_another_address_is_403()
    {
        var result = await Sender().SendAsync(Alice, Content with { From = new EmailAddress("ceo@nightware.localhost") }, true, Ct);

        result.Error!.Code.ShouldBe("ErrorSendAsDenied");
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set" || c.Method == "EmailSubmission/set");
    }

    [Fact]
    public async Task SendMail_without_drafts_stores_in_sent_items()
    {
        _jmap.On("Mailbox/get", """{"accountId":"c","state":"s","list":[{"id":"e","name":"Sent Items","role":"sent"}],"notFound":[]}""");

        (await Sender().SendAsync(Alice, Content, true, Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Single("Email/set")["create"]!["message"]!["mailboxIds"]!.ToJsonString().ShouldBe("""{"e":true}""");
    }

    [Fact]
    public async Task A_failed_context_read_is_returned()
    {
        _jmap.On("Identity/get", _ => [("error", JsonNode.Parse("""{"type":"accountNotSupportedByMethod"}""")!)]);

        (await Sender().SendAsync(Alice, Content, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

/// <summary>Phase 19: a draft created from MIME and a message sent from MIME (<c>Email/import</c> of the uploaded message).</summary>
public sealed class JmapMimeWriteTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");

    private const string Mime = "From: Alice Anderson <alice@nightware.localhost>\r\nTo: bob@nightware.localhost\r\nBcc: carol@example.com\r\n"
        + "Message-ID: <client-chosen@example.com>\r\nSubject: From MIME\r\n\r\nHello\r\n";

    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.OnFixture("Identity/get", "identity-get.json");
        _jmap.StubUpload("Gmime", "message/rfc822", 120);
        _jmap.On("Email/import", """{"accountId":"c","newState":"s2","created":{"draft":{"id":"qnew"},"message":{"id":"qnew"}}}""");
        _jmap.On("EmailSubmission/set", _ =>
        [
            ("EmailSubmission/set", JsonNode.Parse(FakeJmapServer.Read("email-submission-set-created.json"))!),
            ("Email/set", JsonNode.Parse(FakeJmapServer.Read("email-set-implicit-updated.json"))!),
        ]);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private MimeMessage Uploaded() => MimeMessage.Load(new MemoryStream(_jmap.UploadedBody()));

    [Fact]
    public async Task A_MIME_draft_is_imported_into_drafts_with_a_minted_message_id()
    {
        var id = await new JmapDraftStore(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User).CreateFromMimeAsync(Alice, Bytes(Mime), Ct);

        var uploaded = Uploaded();
        uploaded.MessageId.ShouldNotBe("client-chosen@example.com");
        uploaded.MessageId.ShouldEndWith("nightware.localhost");
        uploaded.Subject.ShouldBe("From MIME");
        id.Value.ShouldBe(MailItemId.ForDraft(uploaded.MessageId).ToMessageId());
        _jmap.Single("Email/import")["emails"]!["draft"]!.ToJsonString()
            .ShouldBe("""{"blobId":"Gmime","mailboxIds":{"d":true},"keywords":{"$draft":true,"$seen":true}}""");
    }

    [Fact]
    public async Task A_MIME_message_without_a_sender_gets_the_mailbox_and_a_date()
    {
        await new JmapDraftStore(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User)
            .CreateFromMimeAsync(Alice, Bytes("To: bob@nightware.localhost\r\nSubject: x\r\n\r\nbody\r\n"), Ct);

        var uploaded = Uploaded();
        uploaded.From.Mailboxes.Single().Address.ShouldBe("alice@nightware.localhost");
        uploaded.Headers.Contains(HeaderId.Date).ShouldBeTrue();
    }

    [Fact]
    public async Task Bytes_that_are_not_a_message_are_400()
    {
        var result = await new JmapDraftStore(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User).CreateFromMimeAsync(Alice, new byte[] { 0x00, 0xFF, 0x10 }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_MIME_send_imports_and_submits_in_one_request_then_files_it_in_sent()
    {
        var sender = new JmapMailSender(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User, NullLogger<JmapMailSender>.Instance);

        (await sender.SendMimeAsync(Alice, Bytes(Mime), Ct)).IsSuccess.ShouldBeTrue();

        _jmap.Requests()[^1].ShouldBe(["Email/import", "EmailSubmission/set"]);
        var submission = _jmap.Single("EmailSubmission/set");
        submission["create"]!["send"]!.ToJsonString().ShouldBe("""{"identityId":"b","emailId":"#message"}""");
        submission["onSuccessUpdateEmail"]!["#send"]!["mailboxIds"]!.ToJsonString().ShouldBe("""{"e":true}""");
        Uploaded().Bcc.Mailboxes.Single().Address.ShouldBe("carol@example.com");
    }

    [Fact]
    public async Task Sending_as_someone_else_is_refused_before_anything_is_stored()
    {
        var sender = new JmapMailSender(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User, NullLogger<JmapMailSender>.Instance);

        var result = await sender.SendMimeAsync(Alice, Bytes("From: ceo@example.com\r\nTo: bob@nightware.localhost\r\nSubject: x\r\n\r\nhi\r\n"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/import");
    }

    [Fact]
    public async Task A_failed_submission_discards_the_stored_copy()
    {
        _jmap.On("EmailSubmission/set", """{"accountId":"c","newState":"s3","notCreated":{"send":{"type":"forbiddenToSend"}}}""");
        _jmap.On("Email/set", """{"accountId":"c","oldState":"s2","newState":"s3","destroyed":["qnew"]}""");
        var sender = new JmapMailSender(_jmap.Client(), _jmap.Blobs(), _jmap.Locator, _jmap.User, NullLogger<JmapMailSender>.Instance);

        (await sender.SendMimeAsync(Alice, Bytes(Mime), Ct)).IsSuccess.ShouldBeFalse();

        _jmap.Single("Email/set")["destroy"]!.ToJsonString().ShouldBe("""["qnew"]""");
    }
}

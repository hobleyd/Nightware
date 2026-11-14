using System.Text;
using MimeKit;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapMailImporterTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly DateTimeOffset Received = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 3, 4, 5, 0, 0, TimeSpan.Zero);
    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.OnFixture("Email/import", "email-import-created.json");
        _jmap.StubUpload("Gmime", "message/rfc822", 264);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailImporter Importer() => new(_jmap.Client(), _jmap.Blobs(), _jmap.User, new FixedTime(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero)));

    private static DraftContent Content => new()
    {
        Subject = "Archived: Quarterly numbers",
        Body = new ItemBody(BodyType.Text, "Numbers attached."),
        From = new EmailAddress("alex@example.com", "Alex Sender"),
        ToRecipients = [new EmailAddress("alice@nightware.localhost")],
    };

    private MimeMessage Uploaded() => MimeMessage.Load(new MemoryStream(_jmap.UploadedBody()));

    [Fact]
    public async Task Imports_with_the_original_dates_read_state_and_sender()
    {
        var id = await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content, new ImportOptions(true, Received, Sent), Ct);

        id.Value.ShouldBe(new MessageId("e.beaaaaaj"));
        var import = _jmap.Single("Email/import")["emails"]!["imported"]!.AsObject();
        import["blobId"]!.GetValue<string>().ShouldBe("Gmime");
        import["mailboxIds"]!.ToJsonString().ShouldBe("""{"a":true}""");
        import["keywords"]!.ToJsonString().ShouldBe("""{"$seen":true}""");
        import["receivedAt"]!.GetValue<string>().ShouldBe("2026-03-04T05:06:07Z");

        var mime = Uploaded();
        mime.Date.ShouldBe(Sent);
        mime.From.Mailboxes.ShouldHaveSingleItem().ToString().ShouldBe("\"Alex Sender\" <alex@example.com>");
        mime.To.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("alice@nightware.localhost");
        mime.Subject.ShouldBe("Archived: Quarterly numbers");
        mime.TextBody!.TrimEnd().ShouldBe("Numbers attached.");
        mime.MessageId.ShouldEndWith("@nightware.localhost");
    }

    [Fact]
    public async Task Unread_mail_has_no_keywords_and_a_draft_keeps_its_draft_id()
    {
        (await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content, new ImportOptions(false, Received, Sent), Ct)).IsSuccess.ShouldBeTrue();
        _jmap.Single("Email/import")["emails"]!["imported"]!["keywords"]!.ToJsonString().ShouldBe("{}");
    }

    [Fact]
    public async Task A_draft_import_is_addressed_by_its_draft_id()
    {
        var id = await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Drafts), Content, new ImportOptions(true, null, null, IsDraft: true), Ct);

        id.Value.Value.ShouldStartWith(MailItemId.DraftPrefix);
        _jmap.Single("Email/import")["emails"]!["imported"]!["keywords"]!.ToJsonString().ShouldBe("""{"$seen":true,"$draft":true}""");
        Uploaded().Date.ShouldBe(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), "with no MAPI times the message is dated now");
    }

    [Fact]
    public async Task The_receive_time_dates_the_message_when_the_send_time_is_missing()
    {
        (await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content, new ImportOptions(true, Received, null), Ct)).IsSuccess.ShouldBeTrue();

        Uploaded().Date.ShouldBe(Received);
    }

    [Fact]
    public async Task Without_a_from_the_mailbox_is_the_sender()
    {
        (await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content with { From = null }, new ImportOptions(true, Received, Sent), Ct)).IsSuccess.ShouldBeTrue();

        Uploaded().From.Mailboxes.ShouldHaveSingleItem().Address.ShouldBe("alice@nightware.localhost");
    }

    [Theory]
    [InlineData("msgfolderroot", ErrorKind.Validation)]
    [InlineData("zz", ErrorKind.NotFound)]
    public async Task The_root_and_unknown_folders_are_refused_before_uploading(string folder, ErrorKind kind)
    {
        (await Importer().ImportAsync(Alice, new FolderRef.ById(new FolderId(folder)), Content, new ImportOptions(true, Received, Sent), Ct)).Error!.Kind.ShouldBe(kind);

        _jmap.Host.Requests("/jmap/upload/c/", "POST").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_sender_is_403_so_the_client_retries_without_it()
    {
        _jmap.On("Email/import", """{"accountId":"c","newState":"s","notCreated":{"imported":{"type":"forbiddenFrom"}}}""");

        (await Importer().ImportAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content, new ImportOptions(true, Received, Sent), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class ImportMimeTests
{
    private static readonly EmailAddress From = new("alex@example.com", "Alex");

    [Fact]
    public void Builds_html_with_attachments_inline_resources_threading_and_importance()
    {
        var content = new DraftContent
        {
            Subject = "S",
            Body = new ItemBody(BodyType.Html, "<p>x <img src=\"cid:i1\"></p>"),
            CcRecipients = [new EmailAddress("cc@example.com", "Cc")],
            BccRecipients = [new EmailAddress("bcc@example.com")],
            ReplyTo = [new EmailAddress("r@example.com")],
            InReplyTo = "<p@x>",
            References = ["a@x", "<p@x>"],
            Importance = Importance.High,
            ExtraHeaders = [new InternetHeader("X-Origin", "migration")],
            Attachments =
            [
                new NewAttachment("a.txt", "text/plain", Encoding.ASCII.GetBytes("hi")),
                new NewAttachment("i.png", "image/png", new byte[] { 1, 2 }) { IsInline = true, ContentId = "<i1>" },
            ],
        };

        var mime = ImportMime.Build(content, From, "m@x", DateTimeOffset.UnixEpoch).Value;

        mime.HtmlBody.ShouldBe("<p>x <img src=\"cid:i1\"></p>");
        mime.InReplyTo.ShouldBe("p@x");
        mime.References.ShouldBe(["a@x", "p@x"]);
        mime.Importance.ShouldBe(MessageImportance.High);
        mime.Headers["X-Origin"].ShouldBe("migration");
        mime.Cc.Mailboxes.ShouldHaveSingleItem().Name.ShouldBe("Cc");
        mime.Bcc.Count.ShouldBe(1);
        mime.ReplyTo.Count.ShouldBe(1);
        mime.Attachments.OfType<MimePart>().ShouldHaveSingleItem().FileName.ShouldBe("a.txt");
        mime.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "i1").ContentType.MimeType.ShouldBe("image/png");
    }

    [Fact]
    public void A_missing_body_is_empty_text()
    {
        ImportMime.Build(new DraftContent(), From, "m@x", DateTimeOffset.UnixEpoch).Value.TextBody.ShouldBe(string.Empty);
    }

    [Fact]
    public void An_unparseable_address_is_400()
    {
        ImportMime.Build(new DraftContent { ToRecipients = [new EmailAddress("not an address <<")] }, From, "m@x", DateTimeOffset.UnixEpoch)
            .Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}

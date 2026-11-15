using System.Text;
using System.Text.Json.Nodes;

using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 2 (mail, read side). Ports are faked with domain objects carrying the fixtures' ids; the
/// use cases, mappers, projection and links run for real (the <c>IICalendarCodec</c> too).
/// </summary>
public sealed class MailReadArrangements : IConformanceArrangements
{
    private const string Me = "me@example.com";
    private const string MessageId1 = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAA=";
    private const string MessageId2 = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAAiIsqMbYjsT5e-T7KzowPTAAAYbvZEAAA=";
    private const string InboxId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAA=";
    private const string SentId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEJAAA=";
    private const string TrashId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEKAAA=";
    private const string RootId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEIAAA=";
    private const string ProjectsId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAYbvZFAAA=";
    private const string Conversation1 = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=";
    private const string Conversation2 = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAKF3YB0e1kBVtXQUq0f7Iks=";
    private const string XlsxId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAABEgAQAKz6Oe_TU0RIs2Gu-ZyqAQs=";
    private const string SecondAttachmentId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAABEgAQAB2nJ2D2OlBJopWQ8IC1ngE=";
    private const string CalendarPartId = "4";

    private const string Mime =
        "From: Alex Sender <alex@example.com>\r\nTo: Me <me@example.com>\r\nSubject: Quarterly numbers\r\nDate: Mon, 01 Jun 2026 10:00:00 +0000\r\n"
        + "Message-ID: <a1b2c3@example.com>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nNumbers attached.\r\n";

    private const string Invitation =
        "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:quarterly-review@example.com\r\n"
        + "SUMMARY:Quarterly review\r\nDTSTART:20260610T090000Z\r\nDTEND:20260610T100000Z\r\nLOCATION:Boardroom\r\n"
        + "ORGANIZER;CN=Alex Organiser:mailto:alex@example.com\r\nATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:me@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private static readonly MailboxRef MeRef = new(Me);
    private static readonly DateTimeOffset Received = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 6, 1, 9, 59, 58, TimeSpan.Zero);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.folders.list", static fakes =>
            fakes.For<IMailFolderReader>()
                .ListAsync(MeRef, null, Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
                .Returns(new Page<MailFolder>(
                    [
                        new MailFolder(new FolderId(InboxId), "Inbox", new FolderId(RootId), 1, 3, 120),
                        new MailFolder(new FolderId(TrashId), "Deleted Items", new FolderId(RootId), 0, 0, 7),
                    ],
                    "100")));

        yield return new ConformanceArrangement("mail.folders.get", static fakes =>
            fakes.For<IMailFolderReader>()
                .GetAsync(new MailboxRef("shared@example.com"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(InboxId), "Inbox", new FolderId(RootId), 0, 0, 0)));

        yield return new ConformanceArrangement("mail.folders.list-children", static fakes =>
            fakes.For<IMailFolderReader>()
                .ListAsync(MeRef, new FolderRef.ById(new FolderId(InboxId)), Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
                .Returns(new Page<MailFolder>([new MailFolder(new FolderId(ProjectsId), "Projects", new FolderId(InboxId), 0, 1, 12)])));

        // The folder page offset-pages by $skip; the façade still emits a nextLink (Graph does too).
        yield return new ConformanceArrangement("mail.messages.list-in-folder", static fakes =>
            fakes.For<IMailMessageReader>()
                .ListAsync(MeRef, new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Is<ItemQuery>(q => q.Top == 25 && q.Skip == 0), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([Quarterly(), Lunch()], "25")));

        yield return new ConformanceArrangement("mail.messages.list", static fakes =>
            fakes.For<IMailMessageReader>()
                .ListAsync(MeRef, null, Arg.Is<ItemQuery>(q => q.Filter != null && q.Top == 25), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([Quarterly()])));

        yield return new ConformanceArrangement("mail.messages.list-by-conversation", static fakes =>
            fakes.For<IMailMessageReader>()
                .ListAsync(MeRef, null, Arg.Is<ItemQuery>(q => q.Filter != null && q.Top == 200), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([Quarterly(), SentReply()])));

        yield return new ConformanceArrangement("mail.messages.search", static fakes =>
            fakes.For<IMailMessageReader>()
                .ListAsync(MeRef, null, Arg.Is<ItemQuery>(q => q.Search != null && q.Top == 50), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([Quarterly() with { HasAttachments = true }])));

        yield return new ConformanceArrangement("mail.messages.delta", static fakes =>
            fakes.For<IMailDeltaReader>()
                .DeltaAsync(MeRef, new FolderRef.WellKnown(WellKnownFolder.Inbox), null, Arg.Is<DeltaRequest>(r => r.PageSize == 50), Arg.Any<CancellationToken>())
                .Returns(DeltaPage<MailMessage>.Done(
                    [
                        new DeltaEntry<MailMessage>.Upsert(Quarterly()),
                        new DeltaEntry<MailMessage>.Upsert(Lunch()),
                        new DeltaEntry<MailMessage>.Removed("AAMkAGVmMDEzMTM4-deleted-0001=", RemovedReason.Deleted),
                        new DeltaEntry<MailMessage>.Removed("AAMkAGVmMDEzMTM4-moved-0001=", RemovedReason.Changed),
                    ],
                    new DeltaToken("state-1"))))
        {
            // Graph answers a read/flag change with a partial item (id + changed properties, plus
            // @odata.type). JMAP's Email/changes does not say which properties changed, so the façade
            // sends the whole item; the client treats an item carrying receivedDateTime as an upsert,
            // which applies the same change. Compare the partial item on the properties Graph sent.
            Normalise = static (expected, actual) => KeepOnlyExpectedMembers(expected["value"]![1]!.AsObject(), actual["value"]![1]!.AsObject()),
        };

        yield return new ConformanceArrangement("mail.messages.get", static fakes =>
        {
            var reader = fakes.For<IMailMessageReader>();
            reader.GetAsync(MeRef, new MessageId(MessageId1), Arg.Is<MessageFetch>(f => f.Select == null && f.ExpandAttachments != null), Arg.Any<CancellationToken>())
                .Returns(Invitation1());
            reader.GetAttachmentContentAsync(MeRef, new MessageId(MessageId1), new AttachmentId(CalendarPartId), Arg.Any<CancellationToken>())
                .Returns(Blob("text/calendar", Invitation));
        })
        {
            // Members of the full message the client never reads, which this fixture (trimmed to
            // what the client reads) leaves out.
            IgnorePaths = ["createdDateTime", "lastModifiedDateTime", "categories", "internetMessageId", "isDraft", "sender", "replyTo"],
        };

        yield return new ConformanceArrangement("mail.messages.get-plain-text-body", static fakes =>
            fakes.For<IMailMessageReader>()
                .GetAsync(MeRef, new MessageId(MessageId1), Arg.Is<MessageFetch>(f => f.PreferredBodyType == BodyType.Text && f.Select != null), Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(MessageId1))
                {
                    Body = new ItemBody(BodyType.Text, "Hi,\r\n\r\nThe Q2 numbers are attached.\r\n\r\nAlex"),
                    InternetMessageHeaders =
                    [
                        new InternetHeader("Received", "from mx.example.com by mail.example.com; Mon, 1 Jun 2026 10:00:00 +0000"),
                        new InternetHeader("From", "Alex Sender <alex@example.com>"),
                        new InternetHeader("Subject", "Quarterly numbers"),
                        new InternetHeader("Content-Type", "text/plain; charset=\"utf-8\""),
                    ],
                }));

        yield return new ConformanceArrangement("mail.messages.get-mime", static fakes =>
            fakes.For<IMailMessageReader>()
                .GetRawAsync(MeRef, new MessageId(MessageId1), Arg.Any<CancellationToken>())
                .Returns(Blob("message/rfc822", Mime)));

        yield return new ConformanceArrangement("mail.attachments.list", static fakes =>
            fakes.For<IMailMessageReader>()
                .ListAttachmentsAsync(MeRef, new MessageId(MessageId1), Arg.Any<CancellationToken>())
                .Returns(new List<MailAttachment>
                {
                    new(new AttachmentId(XlsxId), "q2.pdf", "application/pdf", 48213, AttachmentKind.File),
                    new(new AttachmentId(SecondAttachmentId), "invite.ics", "text/calendar", 912, AttachmentKind.File),
                }))
        {
            // With $select, the façade drops @odata.type on collection members (ADR 0001 §14) and never
            // emits Graph's @odata.mediaContentType; the client reads neither here (id/name/size/contentType).
            IgnorePaths = ["value/*/@odata.type", "value/*/@odata.mediaContentType"],
        };

        yield return new ConformanceArrangement("mail.attachments.get", static fakes =>
            fakes.For<IMailMessageReader>()
                .GetAttachmentAsync(MeRef, new MessageId(MessageId1), new AttachmentId(SecondAttachmentId), true, Arg.Any<CancellationToken>())
                .Returns(new MailAttachment(new AttachmentId(SecondAttachmentId), "invite.ics", "text/calendar", 912, AttachmentKind.File)
                {
                    LastModifiedDateTime = Received,
                    ContentBytes = Convert.FromBase64String(AttachmentGetFixtureBytes),
                }))
        {
            // Graph-only members: the media type annotation, contentLocation, and contentId when
            // null (the façade omits a null contentId; the client treats absent and null alike).
            IgnorePaths = ["@odata.mediaContentType", "contentLocation", "contentId"],
        };

        yield return new ConformanceArrangement("mail.attachments.get-value", static fakes =>
            fakes.For<IMailMessageReader>()
                .GetAttachmentContentAsync(MeRef, new MessageId(MessageId1), new AttachmentId(XlsxId), Arg.Any<CancellationToken>())
                .Returns(Blob("message/rfc822", Mime)));
    }

    private const string AttachmentGetFixtureBytes =
        "QkVHSU46VkNBTEVOREFSDQpNRVRIT0Q6UkVRVUVTVA0KVkVSU0lPTjoyLjANClBST0RJRDotLy9FeGFtcGxlLy9FTg0KQkVHSU46VkVWRU5UDQpVSUQ6MDQwMDAwMDA4MjAwRTAwMDc0QzVCNzEwMUE4MkUwMDgwMDAwMDAwMEExQjJDM0Q0RTVGNkQ5MDEwMDAwMDAwMDAwMDAwMDAwMTAwMDAwMDBGMUUyRDNDNEI1QTY5Nzg4DQpTVU1NQVJZOlF1YXJ0ZXJseSByZXZpZXcNCkRUU1RBUlQ6MjAyNjA2MTBUMDkwMDAwWg0KRFRFTkQ6MjAyNjA2MTBUMTAwMDAwWg0KT1JHQU5JWkVSO0NOPUFsZXggT3JnYW5pc2VyOm1haWx0bzphbGV4QGV4YW1wbGUuY29tDQpBVFRFTkRFRTtQQVJUU1RBVD1ORUVEUy1BQ1RJT046bWFpbHRvOm1lQGV4YW1wbGUuY29tDQpFTkQ6VkVWRU5UDQpFTkQ6VkNBTEVOREFSDQo=";

    private static MailMessage Quarterly() => new(new MessageId(MessageId1))
    {
        Subject = "Quarterly numbers",
        From = new EmailAddress("alex@example.com", "Alex Sender"),
        ToRecipients = [new EmailAddress("me@example.com", "Me Example")],
        BodyPreview = "Hi, the Q2 numbers are attached.",
        ReceivedDateTime = Received,
        SentDateTime = Sent,
        ConversationId = Conversation1,
        ParentFolderId = new FolderId(InboxId),
    };

    private static MailMessage Lunch() => Quarterly() with
    {
        Id = new MessageId(MessageId2),
        Subject = "Lunch?",
        ConversationId = Conversation2,
        IsRead = true,
        Flag = new FollowUpFlag(FlagStatus.Flagged),
    };

    private static MailMessage SentReply() => Quarterly() with
    {
        Id = new MessageId("AAMkAGVmMDEzMTM4-sent-copy-0001="),
        Subject = "RE: Quarterly numbers",
        From = new EmailAddress("me@example.com", "Me Example"),
        ToRecipients = [new EmailAddress("alex@example.com", "Alex Sender")],
        IsRead = true,
        ParentFolderId = new FolderId(SentId),
    };

    private static MailMessage Invitation1() => Quarterly() with
    {
        Subject = "Quarterly review",
        HasAttachments = true,
        Body = new ItemBody(BodyType.Html, "<html><body><p>Agenda: Q2 numbers.</p><img src=\"cid:logo@example.com\"></body></html>"),
        CalendarPart = new CalendarPart("REQUEST", new AttachmentId(CalendarPartId)),
        Attachments =
        [
            new MailAttachment(new AttachmentId(XlsxId), "q2.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 18432, AttachmentKind.File),
            new MailAttachment(new AttachmentId(SecondAttachmentId), "logo.png", "image/png", 2048, AttachmentKind.File) { IsInline = true, ContentId = "logo@example.com" },
        ],
    };

    private static BlobContent Blob(string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new BlobContent(contentType, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
    }

    private static void KeepOnlyExpectedMembers(JsonObject expected, JsonObject actual)
    {
        expected.Remove("@odata.type");
        foreach (var key in actual.Select(static p => p.Key).Where(k => !expected.ContainsKey(k)).ToList())
        {
            actual.Remove(key);
        }
    }
}

using System.Text.Json.Nodes;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 3 (mail, write side): one arrangement per operation id in endpoints.json.</summary>
public sealed class MailWriteArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    // The fixtures' Graph ids, passed through opaquely as the façade does with its own.
    private const string MessageId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAA=";
    private const string DraftId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAAiIsqMbYjsT5e-T7KzowPTAAAYbvZEAAA=";
    private const string MovedId = "AAMkAGVmMDEzMTM4-after-move-0001=";
    private const string ImportedId = "AAMkAGVmMDEzMTM4-imported-0001=";
    private const string AttachmentId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAABEgAQAKz6Oe_TU0RIs2Gu-ZyqAQs=";
    private const string InboxId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAA=";
    private const string DeletedItemsId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEKAAA=";
    private const string RootId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEIAAA=";
    private const string ProjectsId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAYbvZFAAA=";
    private const string ConversationId = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=";
    private const string OriginalInternetId = "original-0001@example.com";

    private static readonly DateTimeOffset Received = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 6, 1, 9, 59, 58, TimeSpan.Zero);
    private static readonly DateTimeOffset Migrated = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>
    /// Message fixtures are Graph responses trimmed to what NightMail reads (endpoints.md, "Fixture
    /// format"), while the façade, like Graph, returns the whole message. Compare the members the
    /// fixture pins down, exactly; ignore the rest.
    /// </summary>
    private static readonly Action<JsonNode, JsonNode> FixtureMembersOnly = static (expected, actual) =>
    {
        if (expected is JsonObject e && actual is JsonObject a)
        {
            foreach (var key in a.Select(static p => p.Key).Where(k => !e.ContainsKey(k)).ToList())
            {
                a.Remove(key);
            }
        }
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.messages.update-read-state", static fakes =>
        {
            fakes.For<IMailMessageWriter>()
                .PatchAsync(Me, new MessageId(MessageId), Arg.Is<MessagePatch>(p => p.IsRead == true && p.Flag == null), Arg.Any<CancellationToken>())
                .Returns(Result.Success());
            fakes.For<IMailMessageReader>()
                .GetAsync(Me, new MessageId(MessageId), Arg.Is<MessageFetch>(f => f.Select != null && f.Select.Contains("isRead")), Arg.Any<CancellationToken>())
                .Returns(ReceivedMessage(MessageId, isRead: true, InboxId));
        });

        yield return new ConformanceArrangement("mail.drafts.create", static fakes =>
        {
            fakes.For<IDraftStore>()
                .CreateAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "Draft: Q2 follow-up"
                    && c.Body == new ItemBody(BodyType.Html, "<p>Draft text</p>")
                    && c.ToRecipients.Single().Address == "alex@example.com"
                    && c.CcRecipients.Count == 0), Arg.Any<CancellationToken>())
                .Returns(new MessageId(DraftId));
            fakes.For<IMailMessageReader>()
                .GetAsync(Me, new MessageId(DraftId), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(DraftId))
                {
                    IsDraft = true,
                    Subject = "Draft: Q2 follow-up",
                    ParentFolderId = new FolderId("AAMkAGVmMDEzMTM4-drafts="),
                    ConversationId = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAKF3YB0e1kBVtXQUq0f7Iks=",
                    Body = new ItemBody(BodyType.Html, "<html><body><p>Draft text</p></body></html>"),
                    ToRecipients = [new EmailAddress("alex@example.com")],
                });
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.drafts.update", static fakes =>
        {
            var reader = fakes.For<IMailMessageReader>();

            // NightMail never sends importance, so the draft's own is read rather than reset.
            reader.GetAsync(Me, new MessageId(DraftId), Arg.Is<MessageFetch>(f => f.Select != null && f.Select.SetEquals(new[] { "importance" })), Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(DraftId)) { Importance = Importance.High });
            fakes.For<IDraftStore>()
                .ReplaceAsync(Me, new MessageId(DraftId), Arg.Is<DraftContent>(c => c.Subject == "Draft: Q2 follow-up"
                    && c.Body == new ItemBody(BodyType.Text, "Updated draft text")
                    && c.ToRecipients.Single().Address == "alex@example.com"
                    && c.CcRecipients.Count == 0 && c.BccRecipients.Count == 0
                    && c.Importance == Importance.High), Arg.Any<CancellationToken>())
                .Returns(new MessageId(DraftId));
            reader.GetAsync(Me, new MessageId(DraftId), Arg.Is<MessageFetch>(f => f.Select == null), Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(DraftId)) { IsDraft = true, Subject = "Draft: Q2 follow-up" });
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.messages.delete", static fakes =>
            fakes.For<IMailMessageWriter>()
                .SoftDeleteAsync(Me, new MessageId(DraftId), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("mail.messages.move", static fakes =>
        {
            fakes.For<IMailMessageWriter>()
                .MoveAsync(Me, new MessageId(MessageId), new FolderRef.WellKnown(WellKnownFolder.DeletedItems), Arg.Any<CancellationToken>())
                .Returns(new MessageId(MovedId));
            fakes.For<IMailMessageReader>()
                .GetAsync(Me, new MessageId(MovedId), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(ReceivedMessage(MovedId, isRead: false, DeletedItemsId));
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.messages.permanent-delete", static fakes =>
            fakes.For<IMailMessageWriter>()
                .PermanentDeleteAsync(Me, new MessageId(MessageId), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("mail.messages.create-in-folder", static fakes =>
        {
            fakes.For<IMailImporter>()
                .ImportAsync(
                    Me,
                    new FolderRef.ById(new FolderId(ProjectsId)),
                    Arg.Is<DraftContent>(c => c.Subject == "Archived: Quarterly numbers"
                        && c.Body == new ItemBody(BodyType.Text, "Numbers attached.")
                        && c.From == new EmailAddress("alex@example.com", "Alex Sender")
                        && c.ToRecipients.Single().Address == "me@example.com"),
                    new ImportOptions(IsRead: true, Migrated, Migrated, IsDraft: false),
                    Arg.Any<CancellationToken>())
                .Returns(new MessageId(ImportedId));
            fakes.For<IMailMessageReader>()
                .GetAsync(Me, new MessageId(ImportedId), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(ImportedId))
                {
                    Subject = "Archived: Quarterly numbers",
                    IsDraft = false,
                    IsRead = true,
                    ReceivedDateTime = Migrated,
                    ParentFolderId = new FolderId(ProjectsId),
                });
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.attachments.create", static fakes =>
            fakes.For<IDraftStore>()
                .AddAttachmentAsync(Me, new MessageId(DraftId), Arg.Is<NewAttachment>(a => a.Name == "notes.txt"
                    && a.ContentType == "text/plain"
                    && Text(a.Content) == "Meeting notes\n"
                    && !a.IsInline), Arg.Any<CancellationToken>())
                .Returns(new MailAttachment(new AttachmentId(AttachmentId), "notes.txt", "text/plain", 14, AttachmentKind.File)))
        {
            // Graph includes lastModifiedDateTime; the fixture leaves it out and the client ignores the body.
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.attachments.delete", static fakes =>
            fakes.For<IDraftStore>()
                .RemoveAttachmentAsync(Me, new MessageId(DraftId), new AttachmentId(AttachmentId), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("mail.messages.send-draft", static fakes =>
            fakes.For<IMailSender>()
                .SendDraftAsync(Me, new MessageId(DraftId), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("mail.send-mail", static fakes =>
            fakes.For<IMailSender>()
                .SendAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "Q2 numbers"
                    && c.Body == new ItemBody(BodyType.Html, "<p>See attached.</p>")
                    && c.ToRecipients.Single().Address == "alex@example.com"
                    && c.CcRecipients.Single().Address == "sam@example.com"
                    && c.Attachments.Single().Name == "q2.csv"
                    && Text(c.Attachments.Single().Content) == "q,revenue\n2,100\n"), true, Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        foreach (var (operation, replyAll) in new[] { ("mail.messages.reply", false), ("mail.messages.reply-all", true) })
        {
            yield return new ConformanceArrangement(operation, fakes =>
            {
                ArrangeOriginal(fakes);
                fakes.For<IMailSender>()
                    .SendAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "RE: Quarterly numbers"
                        && c.Body == new ItemBody(BodyType.Html, "<p>Thanks Alex.</p><hr><p>Hi, the Q2 numbers are attached.</p>")
                        && c.ToRecipients.Single().Address == "alex@example.com"
                        && c.CcRecipients.Count == (replyAll ? 1 : 0)
                        && c.InReplyTo == OriginalInternetId), true, Arg.Any<CancellationToken>())
                    .Returns(Result.Success());
            });
        }

        foreach (var (operation, replyAll) in new[] { ("mail.messages.create-reply", false), ("mail.messages.create-reply-all", true) })
        {
            yield return new ConformanceArrangement(operation, fakes =>
            {
                ArrangeOriginal(fakes);
                fakes.For<IDraftStore>()
                    .CreateAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "RE: Quarterly numbers"
                        && c.Body == new ItemBody(BodyType.Text, "Revised file attached.\n\n> Hi, the Q2 numbers are attached.")
                        && c.ToRecipients.Single().Address == "alex@example.com"
                        && c.CcRecipients.Count == (replyAll ? 1 : 0)
                        && c.InReplyTo == OriginalInternetId), Arg.Any<CancellationToken>())
                    .Returns(new MessageId(DraftId));
                fakes.For<IMailMessageReader>()
                    .GetAsync(Me, new MessageId(DraftId), MessageFetch.Default, Arg.Any<CancellationToken>())
                    .Returns(new MailMessage(new MessageId(DraftId)) { IsDraft = true, Subject = "RE: Quarterly numbers", ConversationId = ConversationId });
            })
            {
                Normalise = FixtureMembersOnly,
            };
        }

        yield return new ConformanceArrangement("mail.messages.forward", static fakes =>
        {
            ArrangeOriginal(fakes);
            fakes.For<IMailSender>()
                .SendAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "FW: Quarterly numbers"
                    && c.Body == new ItemBody(BodyType.Html, "<p>FYI</p><hr><p>Hi, the Q2 numbers are attached.</p>")
                    && c.ToRecipients.Single().Address == "sam@example.com"
                    && c.AttachmentsFrom == new MessageId(MessageId)), true, Arg.Any<CancellationToken>())
                .Returns(Result.Success());
        });

        yield return new ConformanceArrangement("mail.messages.create-forward", static fakes =>
        {
            ArrangeOriginal(fakes);
            fakes.For<IDraftStore>()
                .CreateAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "FW: Quarterly numbers"
                    && c.Body == new ItemBody(BodyType.Text, "FYI, without the spreadsheet.")
                    && c.ToRecipients.Single().Address == "sam@example.com"
                    && c.AttachmentsFrom == new MessageId(MessageId)), Arg.Any<CancellationToken>())
                .Returns(new MessageId(DraftId));
            fakes.For<IMailMessageReader>()
                .GetAsync(Me, new MessageId(DraftId), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(DraftId)) { IsDraft = true, Subject = "FW: Quarterly numbers", HasAttachments = true });
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.folders.create-child", static fakes =>
            fakes.For<IMailFolderWriter>()
                .CreateAsync(Me, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Projects", Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(ProjectsId), "Projects", new FolderId(RootId), 0, 0, 0)));

        yield return new ConformanceArrangement("mail.folders.update", static fakes =>
            fakes.For<IMailFolderWriter>()
                .RenameAsync(Me, new FolderId(ProjectsId), "Projects 2026", Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(ProjectsId), "Projects 2026", new FolderId(InboxId), 0, 1, 12)));

        yield return new ConformanceArrangement("mail.folders.delete", static fakes =>
            fakes.For<IMailFolderWriter>()
                .DeleteAsync(Me, new FolderId(ProjectsId), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("mail.folders.move", static fakes =>
            fakes.For<IMailFolderWriter>()
                .MoveAsync(Me, new FolderId(ProjectsId), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(ProjectsId), "Projects", new FolderId(RootId), 0, 1, 12)));
    }

    /// <summary>The message the reply/forward fixtures act on: from Alex to me, cc Sam.</summary>
    private static void ArrangeOriginal(PortFakes fakes) =>
        fakes.For<IMailMessageReader>()
            .GetAsync(Me, new MessageId(MessageId), Arg.Is<MessageFetch>(f => f.Select != null && f.Select.Contains("internetMessageHeaders")), Arg.Any<CancellationToken>())
            .Returns(new MailMessage(new MessageId(MessageId))
            {
                Subject = "Quarterly numbers",
                From = new EmailAddress("alex@example.com", "Alex Sender"),
                ToRecipients = [new EmailAddress("me@example.com", "Me Example")],
                CcRecipients = [new EmailAddress("sam@example.com")],
                InternetMessageId = OriginalInternetId,
            });

    private static string Text(ReadOnlyMemory<byte> bytes) => System.Text.Encoding.UTF8.GetString(bytes.Span);

    private static MailMessage ReceivedMessage(string id, bool isRead, string folderId) => new(new MessageId(id))
    {
        ReceivedDateTime = Received,
        SentDateTime = Sent,
        HasAttachments = false,
        Subject = "Quarterly numbers",
        BodyPreview = "Hi, the Q2 numbers are attached.",
        Importance = Importance.Normal,
        ParentFolderId = new FolderId(folderId),
        ConversationId = ConversationId,
        IsRead = isRead,
        From = new EmailAddress("alex@example.com", "Alex Sender"),
        ToRecipients = [new EmailAddress("me@example.com", "Me Example")],
        Flag = FollowUpFlag.NotFlagged,
    };
}

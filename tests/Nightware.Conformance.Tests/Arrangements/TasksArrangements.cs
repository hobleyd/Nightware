using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Auth;
using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 5B Microsoft To Do: lists, tasks and task attachments (incl. the bearer-less upload PUT).</summary>
public sealed class TasksArrangements : IConformanceArrangements
{
    private const string ListId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAESAAA=";
    private const string SecondListId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAETAAA=";
    private const string TaskIdValue = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAESAAAiIsqMbYjsT5e-T7KzowPTAAAYbx0QAAA=";
    private const string AttachmentIdValue = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAYbx0QAAABEgAQAPsGU-q5x0BMtIaiI4ZjN1Y=";
    private const string SessionToken = "AAMkAGVmMDEzMTM4-session=";
    private const string Mime = "From: Alex Sender <alex@example.com>\r\nTo: Me <me@example.com>\r\nSubject: Quarterly numbers\r\nDate: Mon, 01 Jun 2026 10:00:00 +0000\r\nMessage-ID: <a1b2c3@example.com>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nNumbers attached.\r\n";

    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly TaskListId List = new(ListId);
    private static readonly TaskId Task1 = new(TaskIdValue);
    private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-06-01T10:05:00.1234567Z", CultureInfo.InvariantCulture);

    private static readonly TodoTask Reply = new(Task1, "Reply to Alex about Q2 numbers")
    {
        BodyText = "See the email from Alex.",
        DueDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 5), "UTC"),
        CreatedDateTime = Created,
        LastModifiedDateTime = Created,
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("tasks.lists.list", static fakes =>
            fakes.For<ITaskListReader>()
                .ListAsync(Me, Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
                .Returns(new Page<TaskList>(
                [
                    new TaskList(List, "Tasks") { WellKnownListName = "defaultList" },
                    new TaskList(new TaskListId(SecondListId), "Follow-ups") { WellKnownListName = "none" },
                ])));

        yield return new ConformanceArrangement("tasks.tasks.list", static fakes =>
            fakes.For<ITaskStore>()
                .ListAsync(Me, List, Arg.Is<ItemQuery>(q => q.Top == 200 && q.Filter != null && q.OrderBy.Count == 1), Arg.Any<CancellationToken>())
                .Returns(new Page<TodoTask>([Reply])));

        yield return new ConformanceArrangement("tasks.tasks.create", static fakes =>
            fakes.For<ITaskStore>()
                .CreateAsync(Me, List, Arg.Is<TaskDraft>(d => d.Title == Reply.Title && d.Importance == TaskImportance.Normal
                    && d.BodyText == Reply.BodyText && d.DueDateTime == Reply.DueDateTime), Arg.Any<CancellationToken>())
                .Returns(Reply));

        yield return new ConformanceArrangement("tasks.tasks.update", static fakes =>
            fakes.For<ITaskStore>()
                .UpdateAsync(Me, List, Task1, Arg.Is<TaskDraft>(d => d.Status == TaskStatus.Completed && d.Title == null), Arg.Any<CancellationToken>())
                .Returns(Reply with
                {
                    Status = TaskStatus.Completed,
                    CompletedDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 2, 8, 30, 0), "UTC"),
                }));

        yield return new ConformanceArrangement("tasks.attachments.list", static fakes =>
            fakes.For<ITaskAttachmentStore>()
                .ListAsync(Me, List, Task1, Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<TaskAttachment>>.Success([Attachment(2048) with { LastModifiedDateTime = Modified }])));

        yield return new ConformanceArrangement("tasks.attachments.create", static fakes =>
            fakes.For<ITaskAttachmentStore>()
                .CreateAsync(Me, List, Task1, Arg.Is<NewAttachment>(a => a.Name == "Quarterly numbers.eml" && a.ContentType == "message/rfc822" && a.Content.Length == 245), Arg.Any<CancellationToken>())
                .Returns(Attachment(245) with { LastModifiedDateTime = Modified }));

        yield return new ConformanceArrangement("tasks.attachments.create-upload-session", static fakes =>
            fakes.For<ITaskAttachmentStore>()
                .BeginUploadAsync(Me, List, Task1, "Large thread.eml", "message/rfc822", 4194304, Arg.Any<CancellationToken>())
                .Returns(new UploadSession(SessionToken, DateTimeOffset.Parse("2026-06-01T10:21:00Z", CultureInfo.InvariantCulture), 4194304)))
        {
            // The upload URL is minted per request (it seals the caller's token): check its shape, then compare the rest.
            Normalise = static (expected, actual) =>
            {
                var url = actual["uploadUrl"]!.GetValue<string>();
                url.ShouldStartWith($"{ConformanceHost.GraphBase}/users/me@example.com/todo/lists/{Uri.EscapeDataString(ListId)}/tasks/{Uri.EscapeDataString(TaskIdValue)}/attachmentSessions/{Uri.EscapeDataString(SessionToken)}/content?authtoken=");
                expected["uploadUrl"] = "<uploadUrl>";
                actual["uploadUrl"] = "<uploadUrl>";
            },
        };

        yield return new ConformanceArrangement("tasks.attachments.upload-session-put", static fakes =>
            fakes.For<ITaskAttachmentStore>()
                .PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == SessionToken), Arg.Is(0L), Arg.Any<long>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new UploadProgress([], new TaskAttachment(new AttachmentId(AttachmentIdValue), "Large thread.eml", "message/rfc822", 4194304))))
        {
            // The fixture's URL token is opaque, so the credential source is faked: the PUT arrives without a bearer,
            // exactly as from NightMail, and authenticates only through the self-authorising-URL hook.
            Services = static services => services.AddSingleton<IRequestCredentialSource>(new FixedCredential()),
        };

        yield return new ConformanceArrangement("tasks.attachments.get-value", static fakes =>
            fakes.For<ITaskAttachmentStore>()
                .GetContentAsync(Me, List, Task1, new AttachmentId(AttachmentIdValue), Arg.Any<CancellationToken>())
                .Returns(new BlobContent("message/rfc822", Encoding.UTF8.GetByteCount(Mime), static _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Mime))))));
    }

    private static DateTimeOffset Modified => DateTimeOffset.Parse("2026-06-01T10:06:00Z", CultureInfo.InvariantCulture);

    private static TaskAttachment Attachment(long size) =>
        new(new AttachmentId(AttachmentIdValue), "Quarterly numbers.eml", "message/rfc822", size);

    private sealed class FixedCredential : IRequestCredentialSource
    {
        public bool TryResolve(HttpRequest request, [NotNullWhen(true)] out SealedTicket? ticket)
        {
            ticket = request.Path.Value!.Contains("/attachmentSessions/", StringComparison.Ordinal)
                ? new SealedTicket("conformance-upload-url-token", DateTimeOffset.MaxValue) { Name = "Large thread.eml", Size = 4194304 }
                : null;
            return ticket is not null;
        }
    }
}

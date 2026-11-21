using Nightware.Application.Tasks;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 15 To Do lists, task get/delete and attachment get/delete (ADR 0006 D8), against captures from Microsoft 365.</summary>
public sealed class TaskParityArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly TaskListId ListId = new("AAMk-id-0004=");
    private static readonly TaskId TaskId = new("AAMk-id-0006=");
    private static readonly TaskList List = new(ListId, "Nightware 12fa34") { WellKnownListName = "none" };

    private static readonly TodoTask Task = new(TaskId, "Nightware capture 12fa34")
    {
        Importance = TaskImportance.High,
        CreatedDateTime = DateTimeOffset.Parse("2026-11-20T22:39:06.1680991Z", System.Globalization.CultureInfo.InvariantCulture),
        LastModifiedDateTime = DateTimeOffset.Parse("2026-11-20T22:39:06.231183Z", System.Globalization.CultureInfo.InvariantCulture),
        DueDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 23), "UTC"),
    };

    private static void Known(PortFakes fakes) => fakes.For<ITaskListStore>().GetAsync(Me, ListId, Arg.Any<CancellationToken>()).Returns(List);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("tasks.lists.create", static fakes =>
            fakes.For<ITaskListStore>().CreateAsync(Me, "Nightware 12fa34", Arg.Any<CancellationToken>()).Returns(List));

        yield return new ConformanceArrangement("tasks.lists.get", Known);

        yield return new ConformanceArrangement("tasks.lists.update", static fakes =>
        {
            Known(fakes);
            fakes.For<ITaskListStore>().RenameAsync(Me, ListId, "Nightware 12fa34 renamed", Arg.Any<CancellationToken>())
                .Returns(List with { DisplayName = "Nightware 12fa34 renamed" });
        });

        yield return new ConformanceArrangement("tasks.lists.delete", static fakes =>
        {
            Known(fakes);
            fakes.For<ITaskListStore>().DeleteAsync(Me, ListId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        });

        // Graph writes an empty categories list; the façade omits it, as NightMail's own task fixtures expect.
        yield return new ConformanceArrangement("tasks.tasks.get", static fakes =>
            fakes.For<ITaskStore>().GetAsync(Me, ListId, TaskId, Arg.Any<CancellationToken>()).Returns(Task))
        {
            IgnorePaths = ["categories"],
        };

        yield return new ConformanceArrangement("tasks.tasks.delete", static fakes =>
            fakes.For<ITaskStore>().DeleteAsync(Me, ListId, TaskId, Arg.Any<CancellationToken>()).Returns(Result.Success()));

        var attachment = new TaskAttachment(new AttachmentId("AAMk-id-0010="), "note.txt", "text/plain", 247)
        {
            LastModifiedDateTime = new DateTimeOffset(2026, 11, 20, 22, 39, 9, TimeSpan.Zero),
        };
        yield return new ConformanceArrangement("tasks.attachments.get", fakes =>
        {
            var store = fakes.For<ITaskAttachmentStore>();
            store.ListAsync(Me, ListId, TaskId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<TaskAttachment>>.Success([attachment]));
            store.GetContentAsync(Me, ListId, TaskId, attachment.Id, Arg.Any<CancellationToken>())
                .Returns(new BlobContent("text/plain", null, _ => System.Threading.Tasks.Task.FromResult<Stream>(new MemoryStream("Nightware capture"u8.ToArray()))));
        });

        yield return new ConformanceArrangement("tasks.attachments.delete", static fakes =>
            fakes.For<ITaskAttachmentStore>().DeleteAsync(Me, ListId, TaskId, new AttachmentId("AAMk-id-0010="), Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }
}

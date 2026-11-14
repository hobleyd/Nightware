using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Adapters.Dav.Tests.Tasks.ICal;

public sealed class VTodoCodecTests
{
    private const string NextcloudTask = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Nextcloud Tasks v0.16.1
        BEGIN:VTODO
        UID:3d6f3b4e-tasks-1
        CREATED:20261101T080000Z
        LAST-MODIFIED:20261102T090000Z
        DTSTAMP:20261102T090000Z
        SUMMARY:File the quarterly report
        DESCRIPTION:Numbers from finance\, then submit.
        PRIORITY:1
        STATUS:IN-PROCESS
        PERCENT-COMPLETE:40
        DTSTART;TZID=Europe/Berlin:20261110T090000
        DUE;VALUE=DATE:20261120
        CATEGORIES:Work,Finance
        RRULE:FREQ=MONTHLY;BYMONTHDAY=10
        ATTACH;FMTTYPE=application/pdf:https://cloud.example.com/f/123
        X-APPLE-SORT-ORDER:12345
        BEGIN:VALARM
        ACTION:DISPLAY
        TRIGGER;RELATED=START:-PT1H
        DESCRIPTION:Reminder
        END:VALARM
        END:VTODO
        END:VCALENDAR
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly VTodoCodec _codec = new(new FakeTimeProvider(Now));

    [Fact]
    public void Parses_a_nextcloud_task()
    {
        var parsed = _codec.Parse(NextcloudTask, "calendars/alice/tasks/3d6f.ics").Value.Single();
        parsed.Uid.ShouldBe("3d6f3b4e-tasks-1");
        var task = parsed.Task;
        task.Id.ShouldBe(DavItemId.Resource("calendars/alice/tasks/3d6f.ics").ToTaskId());
        task.Title.ShouldBe("File the quarterly report");
        task.BodyText.ShouldBe("Numbers from finance, then submit.");
        task.Importance.ShouldBe(TaskImportance.High);
        task.Status.ShouldBe(TaskStatus.InProgress);
        task.StartDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 10, 9, 0, 0), "Europe/Berlin"));
        task.DueDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 20), "UTC"));
        task.CompletedDateTime.ShouldBeNull();
        task.IsReminderOn.ShouldBeTrue();
        task.ReminderDateTime.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2026, 11, 10, 7, 0, 0, TimeSpan.Zero)));
        task.Categories.ShouldBe(["Work", "Finance"]);
        task.HasAttachments.ShouldBeTrue();
        task.Recurrence!.Pattern.Type.ShouldBe(RecurrencePatternType.AbsoluteMonthly);
        task.Recurrence.Pattern.DayOfMonth.ShouldBe(10);
        task.CreatedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        task.LastModifiedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("STATUS:NEEDS-ACTION", TaskStatus.NotStarted)]
    [InlineData("STATUS:COMPLETED\r\nCOMPLETED:20261101T000000Z", TaskStatus.Completed)]
    [InlineData("COMPLETED:20261101T000000Z", TaskStatus.Completed)]
    [InlineData("STATUS:CANCELLED", TaskStatus.Deferred)]
    [InlineData("STATUS:NEEDS-ACTION\r\nX-NIGHTWARE-TASK-STATUS:waitingOnOthers", TaskStatus.WaitingOnOthers)]
    [InlineData("STATUS:NEEDS-ACTION\r\nX-NIGHTWARE-TASK-STATUS:deferred", TaskStatus.Deferred)]
    [InlineData("", TaskStatus.NotStarted)]
    public void Status_mapping(string lines, TaskStatus expected) =>
        _codec.Parse(Todo(lines)).Value.Single().Task.Status.ShouldBe(expected);

    [Theory]
    [InlineData("", TaskImportance.Normal)]
    [InlineData("PRIORITY:0", TaskImportance.Normal)]
    [InlineData("PRIORITY:4", TaskImportance.High)]
    [InlineData("PRIORITY:5", TaskImportance.Normal)]
    [InlineData("PRIORITY:6", TaskImportance.Low)]
    [InlineData("PRIORITY:9", TaskImportance.Low)]
    public void Priority_mapping(string line, TaskImportance expected) =>
        _codec.Parse(Todo(line)).Value.Single().Task.Importance.ShouldBe(expected);

    [Fact]
    public void Completed_time_and_absolute_reminder()
    {
        var task = _codec.Parse(Todo("STATUS:COMPLETED\r\nCOMPLETED:20261101T101500Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME:20261031T080000Z\r\nEND:VALARM")).Value.Single().Task;
        task.CompletedDateTime.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2026, 11, 1, 10, 15, 0, TimeSpan.Zero)));
        task.ReminderDateTime.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Creates_a_task_from_a_draft()
    {
        var ics = _codec.Create(new TaskDraft
        {
            Title = "Buy milk",
            Importance = TaskImportance.Low,
            BodyText = "2 litres",
            DueDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 18), "AUS Eastern Standard Time"),
            StartDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 17, 9, 30, 0), "AUS Eastern Standard Time"),
            ReminderDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 17, 8, 0, 0), "UTC"),
            IsReminderOn = true,
            Categories = ["Home"],
            Recurrence = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Tuesday] }, new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 11, 17))),
        }, "task-uid");

        ics.ShouldContain("BEGIN:VTODO");
        ics.ShouldContain("UID:task-uid");
        ics.ShouldContain("DUE;VALUE=DATE:20261118");
        ics.ShouldContain("DTSTART;TZID=Australia/Sydney:20261117T093000");
        ics.ShouldContain("BEGIN:VTIMEZONE");
        ics.ShouldContain("PRIORITY:9");
        ics.ShouldContain("STATUS:NEEDS-ACTION");
        ics.ShouldContain("CREATED:20261115T100000Z");
        ics.ShouldContain("RRULE:FREQ=WEEKLY");

        var task = _codec.Parse(ics).Value.Single().Task;
        task.Title.ShouldBe("Buy milk");
        task.Importance.ShouldBe(TaskImportance.Low);
        task.Status.ShouldBe(TaskStatus.NotStarted);
        task.ReminderDateTime.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero)));
        task.Categories.ShouldBe(["Home"]);
        task.Recurrence!.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Tuesday]);
        task.StartDateTime.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 17, 9, 30, 0), "Australia/Sydney"));
    }

    [Fact]
    public void Apply_patches_in_place_and_keeps_unknown_properties()
    {
        var edited = _codec.Apply(NextcloudTask, new TaskDraft { Title = "File the Q4 report", Status = TaskStatus.Completed, Importance = TaskImportance.Normal }).Value;

        edited.ShouldContain("X-APPLE-SORT-ORDER:12345");
        edited.ShouldContain("ATTACH;FMTTYPE=application/pdf:https://cloud.example.com/f/123");
        edited.ShouldContain("COMPLETED:20261115T100000Z");
        edited.ShouldContain("PERCENT-COMPLETE:100");
        edited.ShouldNotContain("PRIORITY:1");
        var task = _codec.Parse(edited).Value.Single().Task;
        task.Title.ShouldBe("File the Q4 report");
        task.Status.ShouldBe(TaskStatus.Completed);
        task.Importance.ShouldBe(TaskImportance.Normal);
        task.BodyText.ShouldBe("Numbers from finance, then submit.");
        task.Categories.ShouldBe(["Work", "Finance"]);
    }

    [Fact]
    public void Apply_reopens_clears_due_and_reminder_and_round_trips_extension_statuses()
    {
        var completed = _codec.Apply(NextcloudTask, new TaskDraft { Status = TaskStatus.Completed }).Value;
        var reopened = _codec.Apply(completed, new TaskDraft { Status = TaskStatus.WaitingOnOthers, ClearDueDateTime = true, IsReminderOn = false, BodyText = string.Empty }).Value;

        reopened.ShouldNotContain("COMPLETED:");
        reopened.ShouldNotContain("DUE");
        reopened.ShouldNotContain("BEGIN:VALARM");
        var task = _codec.Parse(reopened).Value.Single().Task;
        task.Status.ShouldBe(TaskStatus.WaitingOnOthers);
        task.DueDateTime.ShouldBeNull();
        task.IsReminderOn.ShouldBeFalse();
        task.BodyText.ShouldBeNull();

        var deferred = _codec.Apply(reopened, new TaskDraft { Status = TaskStatus.Deferred }).Value;
        _codec.Parse(deferred).Value.Single().Task.Status.ShouldBe(TaskStatus.Deferred);
        deferred.Split("X-NIGHTWARE-TASK-STATUS").Length.ShouldBe(2);
        _codec.Parse(_codec.Apply(deferred, new TaskDraft { Status = TaskStatus.InProgress }).Value).Value.Single().Task.Status.ShouldBe(TaskStatus.InProgress);
    }

    [Fact]
    public void Serialize_keeps_the_original_completion_time()
    {
        var task = new TodoTask(default, "Done")
        {
            Status = TaskStatus.Completed,
            CompletedDateTime = DateTimeTimeZone.Utc(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero)),
            Importance = TaskImportance.High,
            DueDateTime = new DateTimeTimeZone(new DateTime(2026, 10, 2, 17, 0, 0), "UTC"),
        };
        var ics = _codec.Serialize(task, "u");
        ics.ShouldContain("COMPLETED:20261001T070000Z");
        ics.ShouldContain("DUE:20261002T170000Z");
        var back = _codec.Parse(ics).Value.Single().Task;
        back.Status.ShouldBe(TaskStatus.Completed);
        back.Importance.ShouldBe(TaskImportance.High);
        _codec.Serialize(task with { CompletedDateTime = null, Status = TaskStatus.NotStarted }, "u").ShouldNotContain("COMPLETED");
    }

    [Fact]
    public void Errors()
    {
        _codec.Parse("nope").Error!.Kind.ShouldBe(ErrorKind.Validation);
        _codec.Parse(NextcloudTask, "../x").Error!.Kind.ShouldBe(ErrorKind.Validation);
        _codec.Apply("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nEND:VCALENDAR\r\n", new TaskDraft()).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _codec.Parse(NextcloudTask).Value.Single().Task.Id.Value.ShouldBeEmpty();
    }

    private static string Todo(string lines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VTODO\r\nUID:t\r\nDTSTAMP:20261101T000000Z\r\nSUMMARY:t\r\n"
        + (lines.Length > 0 ? lines + "\r\n" : string.Empty) + "END:VTODO\r\nEND:VCALENDAR\r\n";
}

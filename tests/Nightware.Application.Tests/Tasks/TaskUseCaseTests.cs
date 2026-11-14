using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Application.Tests.Tasks;

public sealed class TaskUseCaseTests
{
    private static readonly MailboxRef Mailbox = new("alice@example.com");
    private static readonly TaskListId List = new("list-1");
    private static readonly TaskId Task1 = new("task-1");
    private static readonly TodoTask Stored = new(Task1, "Reply to Alex");

    private readonly ITaskListReader _lists = Substitute.For<ITaskListReader>();
    private readonly ITaskStore _tasks = Substitute.For<ITaskStore>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_come_from_the_reader_with_the_query()
    {
        var query = new ItemQuery { Top = 100 };
        var page = new Page<TaskList>([new TaskList(List, "Tasks") { WellKnownListName = "defaultList" }]);
        _lists.ListAsync(Mailbox, query, Ct).Returns(page);

        var result = await new ListTaskLists(_lists).ExecuteAsync(Mailbox, query, Ct);

        result.Value.ShouldBeSameAs(page);
    }

    [Fact]
    public async Task Tasks_come_from_the_store_with_the_query()
    {
        var query = new ItemQuery { Top = 200 };
        var page = new Page<TodoTask>([Stored]);
        _tasks.ListAsync(Mailbox, List, query, Ct).Returns(page);

        var result = await new ListTasks(_tasks).ExecuteAsync(Mailbox, List, query, Ct);

        result.Value.ShouldBeSameAs(page);
    }

    [Fact]
    public async Task Create_passes_the_draft_to_the_store()
    {
        var draft = new TaskDraft { Title = "Reply to Alex", DueDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 5), "UTC") };
        _tasks.CreateAsync(Mailbox, List, draft, Ct).Returns(Stored);

        var result = await new CreateTask(_tasks).ExecuteAsync(Mailbox, List, draft, Ct);

        result.Value.ShouldBeSameAs(Stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_requires_a_title(string? title)
    {
        var result = await new CreateTask(_tasks).ExecuteAsync(Mailbox, List, new TaskDraft { Title = title }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("title");
        await _tasks.DidNotReceiveWithAnyArgs().CreateAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task Create_rejects_clearing_a_due_date()
    {
        var result = await new CreateTask(_tasks).ExecuteAsync(Mailbox, List, new TaskDraft { Title = "x", ClearDueDateTime = true }, Ct);

        result.Error!.Target.ShouldBe("dueDateTime");
    }

    [Fact]
    public async Task Create_passes_store_failures_through()
    {
        _tasks.CreateAsync(Mailbox, List, Arg.Any<TaskDraft>(), Ct).Returns(Error.NotFound("no list"));

        var result = await new CreateTask(_tasks).ExecuteAsync(Mailbox, List, new TaskDraft { Title = "x" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Update_patches_the_task()
    {
        var patch = new TaskDraft { Status = TaskStatus.Completed };
        _tasks.UpdateAsync(Mailbox, List, Task1, patch, Ct).Returns(Stored with { Status = TaskStatus.Completed });

        var result = await new UpdateTask(_tasks).ExecuteAsync(Mailbox, List, Task1, patch, Ct);

        result.Value.Status.ShouldBe(TaskStatus.Completed);
    }

    [Fact]
    public async Task Update_with_nothing_to_change_reads_the_task_without_writing()
    {
        _tasks.GetAsync(Mailbox, List, Task1, Ct).Returns(Stored);

        var result = await new UpdateTask(_tasks).ExecuteAsync(Mailbox, List, Task1, new TaskDraft(), Ct);

        result.Value.ShouldBeSameAs(Stored);
        await _tasks.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default, null!, Ct);
    }

    [Fact]
    public async Task Update_clearing_the_due_date_is_a_write()
    {
        var patch = new TaskDraft { ClearDueDateTime = true };
        _tasks.UpdateAsync(Mailbox, List, Task1, patch, Ct).Returns(Stored);

        var result = await new UpdateTask(_tasks).ExecuteAsync(Mailbox, List, Task1, patch, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_rejects_an_empty_title()
    {
        var result = await new UpdateTask(_tasks).ExecuteAsync(Mailbox, List, Task1, new TaskDraft { Title = " " }, Ct);

        result.Error!.Target.ShouldBe("title");
    }
}

using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tests.Tasks;

public sealed class TaskListUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly TaskList Default = new(new TaskListId("d"), "Tasks") { WellKnownListName = "defaultList" };
    private static readonly TaskList Mine = new(new TaskListId("m"), "Mine") { WellKnownListName = "none" };
    private static readonly TaskList Shared = new(new TaskListId("s"), "Shared") { WellKnownListName = "none", IsOwner = false, IsShared = true };
    private readonly ITaskListStore _lists = Substitute.For<ITaskListStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TaskListsUseCase UseCase => new(_lists);

    public TaskListUseCaseTests()
    {
        foreach (var list in new[] { Default, Mine, Shared })
        {
            _lists.GetAsync(Me, list.Id, Arg.Any<CancellationToken>()).Returns(list);
        }
    }

    [Fact]
    public async Task A_list_needs_a_name()
    {
        _lists.CreateAsync(Me, "Groceries", Ct).Returns(Mine with { DisplayName = "Groceries" });

        (await UseCase.CreateAsync(Me, " Groceries ", Ct)).Value.DisplayName.ShouldBe("Groceries");
        (await UseCase.CreateAsync(Me, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.RenameAsync(Me, Mine.Id, "", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task The_default_list_and_shared_lists_are_neither_renamed_nor_deleted()
    {
        (await UseCase.RenameAsync(Me, Default.Id, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.DeleteAsync(Me, Default.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await UseCase.RenameAsync(Me, Shared.Id, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await UseCase.DeleteAsync(Me, Shared.Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _lists.DidNotReceiveWithAnyArgs().DeleteAsync(default, default, Ct);
    }

    [Fact]
    public async Task Own_lists_are_renamed_and_deleted()
    {
        _lists.RenameAsync(Me, Mine.Id, "Chores", Ct).Returns(Mine with { DisplayName = "Chores" });
        _lists.DeleteAsync(Me, Mine.Id, Ct).Returns(Result.Success());
        _lists.GetAsync(Me, new TaskListId("nope"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await UseCase.RenameAsync(Me, Mine.Id, "Chores", Ct)).Value.DisplayName.ShouldBe("Chores");
        (await UseCase.DeleteAsync(Me, Mine.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await UseCase.DeleteAsync(Me, new TaskListId("nope"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Tasks_are_got_and_deleted_through_the_store()
    {
        var tasks = Substitute.For<ITaskStore>();
        var task = new TodoTask(new TaskId("t"), "Do it");
        tasks.GetAsync(Me, Mine.Id, task.Id, Ct).Returns(task);
        tasks.DeleteAsync(Me, Mine.Id, task.Id, Ct).Returns(Result.Success());

        (await new GetTask(tasks).ExecuteAsync(Me, Mine.Id, task.Id, Ct)).Value.ShouldBe(task);
        (await new DeleteTask(tasks).ExecuteAsync(Me, Mine.Id, task.Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task One_attachment_is_read_with_its_bytes()
    {
        var store = Substitute.For<ITaskAttachmentStore>();
        var task = new TaskId("t");
        var attachment = new TaskAttachment(new AttachmentId("a"), "note.txt", "text/plain", 3);
        store.ListAsync(Me, Mine.Id, task, Ct).Returns(Result<IReadOnlyList<TaskAttachment>>.Success([attachment]));
        store.GetContentAsync(Me, Mine.Id, task, attachment.Id, Ct)
            .Returns(new BlobContent("text/plain", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))));
        store.DeleteAsync(Me, Mine.Id, task, attachment.Id, Ct).Returns(Result.Success());

        var found = (await new GetTaskAttachment(store).ExecuteAsync(Me, Mine.Id, task, attachment.Id, Ct)).Value;
        found.Attachment.ShouldBe(attachment);
        found.Content.ShouldBe(new byte[] { 1, 2, 3 });
        (await new GetTaskAttachment(store).ExecuteAsync(Me, Mine.Id, task, new AttachmentId("nope"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new DeleteTaskAttachment(store).ExecuteAsync(Me, Mine.Id, task, attachment.Id, Ct)).IsSuccess.ShouldBeTrue();
    }
}

public sealed class TaskPartsUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly TaskListId List = new("l");
    private static readonly TaskId Task = new("t");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_checklist_item_needs_a_name()
    {
        var store = Substitute.For<IChecklistStore>();
        var item = new ChecklistItem("i", "Step", false);
        store.CreateAsync(Me, List, Task, "Step", false, Ct).Returns(item);
        store.UpdateAsync(Me, List, Task, "i", null, true, Ct).Returns(item with { IsChecked = true });
        store.DeleteAsync(Me, List, Task, "i", Ct).Returns(Result.Success());
        var useCase = new ChecklistItemsUseCase(store);

        (await useCase.CreateAsync(Me, List, Task, " Step ", null, Ct)).Value.ShouldBe(item);
        (await useCase.CreateAsync(Me, List, Task, null, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.UpdateAsync(Me, List, Task, "i", null, true, Ct)).Value.IsChecked.ShouldBeTrue();
        (await useCase.UpdateAsync(Me, List, Task, "i", " ", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.DeleteAsync(Me, List, Task, "i", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_linked_resources_url_must_be_absolute()
    {
        var store = Substitute.For<ILinkedResourceStore>();
        var resource = new LinkedResource("") { WebUrl = "https://example.com/1" };
        store.CreateAsync(Me, List, Task, resource, Ct).Returns(resource with { Id = "r" });
        store.DeleteAsync(Me, List, Task, "r", Ct).Returns(Result.Success());
        var useCase = new LinkedResourcesUseCase(store);

        (await useCase.CreateAsync(Me, List, Task, resource, Ct)).Value.Id.ShouldBe("r");
        (await useCase.CreateAsync(Me, List, Task, resource with { WebUrl = "not a url" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.UpdateAsync(Me, List, Task, "r", resource with { WebUrl = "/relative" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.DeleteAsync(Me, List, Task, "r", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task One_item_is_read_from_the_tasks_list_of_them()
    {
        var checklist = Substitute.For<IChecklistStore>();
        checklist.ListAsync(Me, List, Task, Ct).Returns(Result<IReadOnlyList<ChecklistItem>>.Success([new ChecklistItem("a", "One", false), new ChecklistItem("b", "Two", true)]));
        var links = Substitute.For<ILinkedResourceStore>();
        links.ListAsync(Me, List, Task, Ct).Returns(Result<IReadOnlyList<LinkedResource>>.Success([new LinkedResource("r") { DisplayName = "Item" }]));

        (await new ChecklistItemsUseCase(checklist).GetAsync(Me, List, Task, "b", Ct)).Value.DisplayName.ShouldBe("Two");
        (await new ChecklistItemsUseCase(checklist).GetAsync(Me, List, Task, "B", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound, "ids are exact");
        (await new LinkedResourcesUseCase(links).GetAsync(Me, List, Task, "r", Ct)).Value.DisplayName.ShouldBe("Item");
        (await new LinkedResourcesUseCase(links).GetAsync(Me, List, Task, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_task_that_cannot_be_read_fails_the_single_read_the_same_way()
    {
        var checklist = Substitute.For<IChecklistStore>();
        checklist.ListAsync(Me, List, Task, Ct).Returns(Result<IReadOnlyList<ChecklistItem>>.Failure(Error.NotFound("no task")));

        (await new ChecklistItemsUseCase(checklist).GetAsync(Me, List, Task, "a", Ct)).Error!.Message.ShouldBe("no task");
    }
}

public sealed class TaskDeltaUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_lists_delta_pages_first_then_reports_renames_and_removals()
    {
        var reader = Substitute.For<ITaskListReader>();
        var a = new TaskList(new TaskListId("a"), "A") { WellKnownListName = "defaultList" };
        var b = new TaskList(new TaskListId("b"), "B") { WellKnownListName = "none" };
        reader.ListAsync(Me, Arg.Any<Nightware.Application.Common.Query.ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<TaskList>([a, b]));
        var sync = new SyncTaskLists(reader);

        var initial = (await sync.ExecuteAsync(Me, null, Ct)).Value;
        initial.Entries.Count.ShouldBe(2);
        initial.NextPage.ShouldNotBeNull();
        var settled = (await sync.ExecuteAsync(Me, initial.NextPage, Ct)).Value;
        settled.Entries.ShouldBeEmpty();
        settled.Final.ShouldNotBeNull();

        reader.ListAsync(Me, Arg.Any<Nightware.Application.Common.Query.ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<TaskList>([a with { DisplayName = "A2" }]));
        var changes = (await sync.ExecuteAsync(Me, settled.Final, Ct)).Value;
        changes.Entries.OfType<DeltaEntry<TaskList>.Upsert>().ShouldHaveSingleItem().Item.DisplayName.ShouldBe("A2");
        changes.Entries.OfType<DeltaEntry<TaskList>.Removed>().ShouldHaveSingleItem().Id.ShouldBe("b");
        (await sync.ExecuteAsync(Me, new DeltaToken("!!"), Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public async Task The_tasks_delta_passes_to_the_port()
    {
        var delta = Substitute.For<ITaskDelta>();
        var page = DeltaPage<TodoTask>.Done([], new DeltaToken("t"));
        delta.DeltaAsync(Me, new TaskListId("l"), null, Ct).Returns(page);

        (await new SyncTasks(delta).ExecuteAsync(Me, new TaskListId("l"), null, Ct)).Value.ShouldBe(page);
    }
}

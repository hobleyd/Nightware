using Nightware.Adapters.Dav.Tasks;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;
using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

using SortDirection = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Adapters.Dav.Tests.Tasks;

public sealed class TaskQueryEvaluatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TodoTask[] Tasks =
    [
        new(new TaskId("a"), "banana") { Status = TaskStatus.Completed, CreatedDateTime = Day.AddDays(1), DueDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 9), "UTC") },
        new(new TaskId("b"), "Apple") { Status = TaskStatus.NotStarted, CreatedDateTime = Day.AddDays(3) },
        new(new TaskId("c"), "cherry") { Status = TaskStatus.WaitingOnOthers, CreatedDateTime = Day.AddDays(2), DueDateTime = new DateTimeTimeZone(new DateTime(2026, 11, 7), "UTC") },
    ];

    private static IEnumerable<string> Ids(Result<Page<TodoTask>> result) => result.Value.Items.Select(t => t.Id.Value);

    [Theory]
    [InlineData(CompareOp.Ne, "completed", new[] { "b", "c" })]
    [InlineData(CompareOp.Eq, "completed", new[] { "a" })]
    [InlineData(CompareOp.Eq, "WAITINGONOTHERS", new[] { "c" })]
    public void Filters_on_status(CompareOp op, string status, string[] expected)
    {
        var query = new ItemQuery { Filter = new FilterNode.Compare("status", op, new FilterValue.Text(status)) };

        Ids(TaskQueryEvaluator.Apply(Tasks, query)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("microsoft.graph.taskStatus'completed'")]
    [InlineData("completed")]
    public void Accepts_enum_literals(string literal)
    {
        var query = new ItemQuery { Filter = new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.EnumMember(literal)) };

        Ids(TaskQueryEvaluator.Apply(Tasks, query)).ShouldBe(["a"]);
    }

    [Fact]
    public void Combines_conjuncts()
    {
        var query = new ItemQuery
        {
            Filter = new FilterNode.And(
                new FilterNode.Compare("status", CompareOp.Ne, new FilterValue.Text("completed")),
                new FilterNode.Compare("status", CompareOp.Ne, new FilterValue.Text("notStarted"))),
        };

        Ids(TaskQueryEvaluator.Apply(Tasks, query)).ShouldBe(["c"]);
    }

    public static TheoryData<FilterNode> Unsupported() =>
    [
        new FilterNode.Or(new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.Text("completed")), new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.Text("deferred"))),
        new FilterNode.Compare("status", CompareOp.Gt, new FilterValue.Text("completed")),
        new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.Text("finished")),
        new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.Text("1")),
        new FilterNode.Compare("title", CompareOp.Eq, new FilterValue.Text("x")),
        new FilterNode.Not(new FilterNode.Compare("status", CompareOp.Eq, new FilterValue.Text("completed"))),
    ];

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void Refuses_what_it_cannot_evaluate(FilterNode filter)
    {
        var result = TaskQueryEvaluator.Apply(Tasks, new ItemQuery { Filter = filter });

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("$filter");
    }

    [Theory]
    [InlineData("createdDateTime", SortDirection.Descending, new[] { "b", "c", "a" })]
    [InlineData("createdDateTime", SortDirection.Ascending, new[] { "a", "c", "b" })]
    [InlineData("title", SortDirection.Ascending, new[] { "b", "a", "c" })]
    [InlineData("dueDateTime/dateTime", SortDirection.Ascending, new[] { "b", "c", "a" })]
    [InlineData("lastModifiedDateTime", SortDirection.Ascending, new[] { "a", "b", "c" })]
    public void Orders(string property, SortDirection direction, string[] expected)
    {
        var query = new ItemQuery { OrderBy = [new OrderBy(property, direction)] };

        Ids(TaskQueryEvaluator.Apply(Tasks, query)).ShouldBe(expected);
    }

    [Fact]
    public void Orders_by_several_keys()
    {
        var query = new ItemQuery { OrderBy = [new OrderBy("lastModifiedDateTime", SortDirection.Ascending), new OrderBy("title", SortDirection.Descending)] };

        Ids(TaskQueryEvaluator.Apply(Tasks, query)).ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public void Refuses_an_unknown_ordering()
    {
        var result = TaskQueryEvaluator.Apply(Tasks, new ItemQuery { OrderBy = [new OrderBy("importance", SortDirection.Ascending)] });

        result.Error!.Target.ShouldBe("$orderby");
    }

    [Fact]
    public void Pages_by_offset()
    {
        var first = TaskQueryEvaluator.Apply(Tasks, new ItemQuery { Top = 2 }).Value;
        first.Items.Select(t => t.Id.Value).ShouldBe(["a", "b"]);
        first.SkipToken.ShouldBe("2");

        var second = TaskQueryEvaluator.Apply(Tasks, new ItemQuery { Top = 2, SkipToken = first.SkipToken }).Value;
        second.Items.Select(t => t.Id.Value).ShouldBe(["c"]);
        second.SkipToken.ShouldBeNull();
    }

    [Fact]
    public void Honours_skip_unless_a_skiptoken_is_given()
    {
        Ids(TaskQueryEvaluator.Apply(Tasks, new ItemQuery { Skip = 2 })).ShouldBe(["c"]);
        Ids(TaskQueryEvaluator.Apply(Tasks, new ItemQuery { Skip = 2, SkipToken = "1" })).ShouldBe(["b", "c"]);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("-1")]
    public void Refuses_a_bad_skiptoken(string token) =>
        TaskQueryEvaluator.Apply(Tasks, new ItemQuery { SkipToken = token }).Error!.Target.ShouldBe("$skiptoken");
}

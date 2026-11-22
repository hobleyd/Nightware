using Nightware.Adapters.Directory.Directory;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Directory;

public sealed class DirectoryPagingTests
{
    [Fact]
    public void The_offset_is_the_skiptoken()
    {
        DirectoryPaging.Offset(new ItemQuery(), "groups").Value.ShouldBe(0);
        DirectoryPaging.Offset(new ItemQuery { SkipToken = "40" }, "groups").Value.ShouldBe(40);
        DirectoryPaging.Offset(new ItemQuery { SkipToken = "x" }, "groups").Error!.Target.ShouldBe("$skiptoken");
    }

    [Fact]
    public void Options_other_than_select_top_and_skiptoken_are_refused()
    {
        DirectoryPaging.Offset(new ItemQuery { Count = true }, "groups").Error!.Kind.ShouldBe(ErrorKind.Validation);
        DirectoryPaging.Offset(new ItemQuery { OrderBy = [new OrderBy("displayName", Nightware.Application.Common.Query.SortDirection.Ascending)] }, "groups").Error!.Target.ShouldBe("$orderby");
    }

    [Fact]
    public void A_slice_has_a_next_offset_only_while_more_remain()
    {
        int[] all = [1, 2, 3];

        var first = DirectoryPaging.Slice(all, 0, 2);
        var last = DirectoryPaging.Slice(all, 2, 2);

        first.Items.ShouldBe([1, 2]);
        first.SkipToken.ShouldBe("2");
        last.Items.ShouldBe([3]);
        last.SkipToken.ShouldBeNull();
        DirectoryPaging.Top(new ItemQuery()).ShouldBe(DirectoryPaging.DefaultPageSize);
    }
}

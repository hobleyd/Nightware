using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class PagingTests
{
    [Fact]
    public void The_skip_token_wins_over_skip()
    {
        Paging.Offset(new ItemQuery { Skip = 25 }).Value.ShouldBe(25);
        Paging.Offset(new ItemQuery { Skip = 25, SkipToken = "50" }).Value.ShouldBe(50);
        Paging.Offset(ItemQuery.Default).Value.ShouldBe(0);
        Paging.Offset(new ItemQuery { SkipToken = "-1" }).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Size_defaults_to_ten_and_is_capped()
    {
        Paging.Size(ItemQuery.Default, 500).ShouldBe(10);
        Paging.Size(new ItemQuery { Top = 999 }, 500).ShouldBe(500);
    }

    [Fact]
    public void Next_is_set_only_while_more_remain()
    {
        Paging.Next(0, 25, 26).ShouldBe("25");
        Paging.Next(25, 1, 26).ShouldBeNull();
        Paging.Next(0, 0, 10).ShouldBeNull();
    }
}

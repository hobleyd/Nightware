using Nightware.Adapters.Dav.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class OffsetPagingTests
{
    private static readonly int[] Items = [1, 2, 3, 4, 5];

    [Fact]
    public void Pages_by_top_and_continues_from_the_token()
    {
        var first = OffsetPaging.Slice(Items, new ItemQuery { Top = 2 }, 10).Value;
        first.Items.ShouldBe([1, 2]);
        first.SkipToken.ShouldBe("2");
        OffsetPaging.Slice(Items, new ItemQuery { Top = 2, SkipToken = "4", Skip = 1 }, 10).Value.Items.ShouldBe([5]);
    }

    [Fact]
    public void Uses_skip_and_the_default_size()
    {
        var page = OffsetPaging.Slice(Items, new ItemQuery { Skip = 3 }, 10).Value;
        page.Items.ShouldBe([4, 5]);
        page.HasMore.ShouldBeFalse();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("-1")]
    public void Rejects_a_token_it_did_not_mint(string token) =>
        OffsetPaging.Slice(Items, new ItemQuery { SkipToken = token }, 10).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public void Count_is_every_item_not_the_page()
    {
        OffsetPaging.Slice(Items, new ItemQuery { Top = 2, Count = true }, 10).Value.Total.ShouldBe(5);
        OffsetPaging.Slice(Items, new ItemQuery { Top = 2 }, 10).Value.Total.ShouldBeNull();
    }
}

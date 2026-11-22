using Nightware.Domain.Files;

namespace Nightware.Domain.Tests.Files;

public sealed class DriveTests
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("/", new string[0])]
    [InlineData("/Documents/a b.txt", new[] { "Documents", "a b.txt" })]
    [InlineData("Documents/", new[] { "Documents" })]
    public void A_path_splits_into_its_segments(string? path, string[] expected)
    {
        DriveItemRef.TryParsePath(path, out var segments).ShouldBeTrue();
        segments.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/a//b")]
    [InlineData("/a/../b")]
    [InlineData("/./b")]
    [InlineData("/a\\b")]
    [InlineData("/a\u0000b")]
    public void A_path_that_could_name_something_else_is_refused(string path)
    {
        DriveItemRef.TryParsePath(path, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_path_too_deep_or_a_segment_too_long_is_refused()
    {
        DriveItemRef.TryParsePath(string.Join('/', Enumerable.Repeat("a", DriveItemRef.MaxPathSegments + 1)), out _).ShouldBeFalse();
        DriveItemRef.IsValidName(new string('a', DriveItemRef.MaxSegmentLength + 1)).ShouldBeFalse();
        DriveItemRef.IsValidName(new string('a', DriveItemRef.MaxSegmentLength)).ShouldBeTrue();
    }

    [Fact]
    public void A_reference_knows_the_root_and_grows_by_child()
    {
        DriveItemRef.Root.IsRoot.ShouldBeTrue();
        var child = DriveItemRef.Root.Child("Documents");
        child.IsRoot.ShouldBeFalse();
        child.Path.ShouldBe(["Documents"]);
        DriveItemRef.ById("7").IsRoot.ShouldBeFalse();
    }

    [Theory]
    [InlineData(10L, 90L, 100L, "normal")]
    [InlineData(95L, 5L, 100L, "nearing")]
    [InlineData(1000L, 0L, 1000L, "exceeded")]
    [InlineData(9995L, 5L, 10000L, "critical")]
    [InlineData(10L, null, null, "normal")]
    public void Quota_state_follows_what_is_left(long used, long? remaining, long? total, string state)
    {
        new DriveQuota(used, remaining, total).State.ShouldBe(state);
    }

    [Fact]
    public void A_deleted_entry_carries_only_its_id()
    {
        var deleted = DriveItem.Deleted("42");

        deleted.IsDeleted.ShouldBeTrue();
        deleted.Id.ShouldBe("42");
    }

    [Theory]
    [InlineData(ThumbnailSize.Small, 96)]
    [InlineData(ThumbnailSize.Medium, 176)]
    [InlineData(ThumbnailSize.Large, 800)]
    public void Thumbnail_sizes_have_graphs_edges(ThumbnailSize size, int edge)
    {
        ThumbnailSizes.EdgeOf(size).ShouldBe(edge);
    }
}

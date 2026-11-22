using Nightware.Adapters.Dav.Files.Drive;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class DriveHomeTests
{
    [Fact]
    public void The_drive_id_is_the_decoded_uid_in_base64url()
    {
        new DriveHome("alice").DriveId.ShouldBe("nc!YWxpY2U");
        new DriveHome("a%20b").DriveId.ShouldBe(new DriveHome("a%20b").DriveId);
        new DriveHome("a%20b").DriveId.ShouldBe("nc!YSBi");
    }

    [Theory]
    [InlineData("files/alice/", "/")]
    [InlineData("files/alice", "/")]
    [InlineData("files/alice/Docs/", "/Docs")]
    [InlineData("files/alice/Docs/a%20b.txt", "/Docs/a b.txt")]
    [InlineData("files/bob/x", null)]
    [InlineData("files/alicex/", null)]
    public void A_path_below_the_home_is_decoded(string href, string? expected) =>
        new DriveHome("alice").PathOf(href).ShouldBe(expected);

    [Fact]
    public void Its_hrefs_are_the_files_and_uploads_homes()
    {
        var home = new DriveHome("alice");

        home.Href.ShouldBe("files/alice/");
        home.UploadsHref.ShouldBe("uploads/alice/");
    }
}

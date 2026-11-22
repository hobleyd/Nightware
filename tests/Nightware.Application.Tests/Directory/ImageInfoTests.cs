using Nightware.Application.Directory;

namespace Nightware.Application.Tests.Directory;

public sealed class ImageInfoTests
{
    [Fact]
    public void A_png_reads_its_size_from_IHDR() =>
        ImageInfo.Read(TestImages.Png(40, 20)).ShouldBe(new ImageInfo(ImageInfo.Png, 40, 20));

    [Fact]
    public void A_jpeg_reads_its_size_from_the_frame_header()
    {
        var info = ImageInfo.Read(TestImages.Jpeg8)!;

        info.ShouldBe(new ImageInfo(ImageInfo.Jpeg, 8, 8));
        info.IsSquare.ShouldBeTrue();
    }

    [Fact]
    public void Another_format_or_a_truncated_header_is_null()
    {
        ImageInfo.Read("GIF89a\x01\x00\x01\x00"u8).ShouldBeNull();
        ImageInfo.Read(TestImages.Png(4, 4).AsSpan(0, 20)).ShouldBeNull();
        ImageInfo.Read(TestImages.Jpeg8.AsSpan(0, 40)).ShouldBeNull();
        ImageInfo.Read([]).ShouldBeNull();
    }

    [Fact]
    public void A_jpeg_whose_scan_comes_before_a_frame_or_with_a_broken_marker_is_null()
    {
        ImageInfo.Read([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x00, 0x00]).ShouldBeNull();
        ImageInfo.Read([0xFF, 0xD8, 0x12, 0x34, 0x00, 0x02]).ShouldBeNull();
        ImageInfo.Read([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0x00, 0x00]).ShouldBeNull();
    }

    [Fact]
    public void Fill_bytes_and_standalone_markers_are_skipped()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xFF, 0xD0, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x10, 0x00, 0x20, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        ImageInfo.Read(jpeg).ShouldBe(new ImageInfo(ImageInfo.Jpeg, 32, 16));
    }

    [Fact]
    public void A_zero_size_is_null() => ImageInfo.Read(TestImages.Png(0, 4)).ShouldBeNull();
}

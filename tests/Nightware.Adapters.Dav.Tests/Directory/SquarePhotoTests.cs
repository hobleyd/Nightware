using System.Buffers.Binary;

using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

using SkiaSharp;

namespace Nightware.Adapters.Dav.Tests.Directory;

public sealed class SquarePhotoTests
{
    /// <summary>A <paramref name="width"/> × <paramref name="height"/> image: the left half red, the right half blue.</summary>
    private static byte[] Halves(int width, int height, SKEncodedImageFormat format, bool transparent = false)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = transparent ? new SKColor(0, 0, 255, 128) : SKColors.Blue };
            canvas.DrawRect(0, 0, width / 2f, height, red);
            canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        }

        using var data = bitmap.Encode(format, 95);
        return data.ToArray();
    }

    private static SKBitmap Decode(ReadOnlyMemory<byte> image) => SKBitmap.Decode(image.ToArray());

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Blue < 60;

    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60;

    [Fact]
    public void A_square_jpeg_or_png_passes_untouched()
    {
        var png = Halves(32, 32, SKEncodedImageFormat.Png);

        var (image, type) = SquarePhoto.From(png).Value;

        type.ShouldBe(ImageInfo.Png);
        image.ToArray().ShouldBe(png);
    }

    [Fact]
    public void A_wide_opaque_photo_is_centre_cropped_to_a_square_jpeg()
    {
        var (image, type) = SquarePhoto.From(Halves(400, 200, SKEncodedImageFormat.Jpeg)).Value;

        type.ShouldBe(ImageInfo.Jpeg);
        using var square = Decode(image);
        (square.Width, square.Height).ShouldBe((200, 200));
        IsRed(square.GetPixel(10, 100)).ShouldBeTrue();
        IsBlue(square.GetPixel(190, 100)).ShouldBeTrue();
    }

    [Fact]
    public void A_photo_with_transparency_stays_a_png_and_a_gif_is_converted()
    {
        var (png, pngType) = SquarePhoto.From(Halves(60, 30, SKEncodedImageFormat.Png, transparent: true)).Value;
        pngType.ShouldBe(ImageInfo.Png);
        using (var square = Decode(png))
        {
            (square.Width, square.Height).ShouldBe((30, 30));
        }

        var webp = Halves(30, 60, SKEncodedImageFormat.Webp);
        var (converted, convertedType) = SquarePhoto.From(webp).Value;
        convertedType.ShouldBeOneOf(ImageInfo.Jpeg, ImageInfo.Png);
        using var fromWebp = Decode(converted);
        (fromWebp.Width, fromWebp.Height).ShouldBe((30, 30));
    }

    [Fact]
    public void A_large_photo_comes_out_at_most_1024_a_side()
    {
        var (image, _) = SquarePhoto.From(Halves(3000, 2000, SKEncodedImageFormat.Jpeg)).Value;

        using var square = Decode(image);
        (square.Width, square.Height).ShouldBe((SquarePhoto.MaxEdge, SquarePhoto.MaxEdge));
    }

    [Fact]
    public void A_sideways_phone_photo_is_turned_upright_by_its_exif_orientation_before_cropping()
    {
        // Stored 40x20 (left red, right blue) with orientation 6: shown turned a quarter clockwise, 20x40 with red on
        // top. The centre square is then red above blue; ignoring the tag would give red beside blue.
        var jpeg = WithOrientation(Halves(40, 20, SKEncodedImageFormat.Jpeg), 6);

        var (image, _) = SquarePhoto.From(jpeg).Value;

        using var square = Decode(image);
        (square.Width, square.Height).ShouldBe((20, 20));
        IsRed(square.GetPixel(10, 2)).ShouldBeTrue();
        IsBlue(square.GetPixel(10, 17)).ShouldBeTrue();
    }

    [Fact]
    public void An_image_claiming_a_huge_canvas_is_refused_before_it_is_decoded()
    {
        // A PNG header for 30000 x 20000 (600 megapixels) with no pixel data at all.
        var header = Convert.FromHexString("89504E470D0A1A0A0000000D49484452").Concat(BigEndian(30000)).Concat(BigEndian(20000))
            .Concat(Convert.FromHexString("0802000000")).ToArray();
        var png = header.Concat(BigEndian(unchecked((int)Crc32(header.AsSpan(12)))))
            .Concat(Chunk("IDAT", Deflate(new byte[256])))
            .Concat(Chunk("IEND", []))
            .ToArray();

        var error = SquarePhoto.From(png).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain("30000x20000");
    }

    [Fact]
    public void Bytes_that_are_no_image_are_400()
    {
        SquarePhoto.From("not an image at all"u8.ToArray()).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        return [.. BigEndian(data.Length), .. typed, .. BigEndian(unchecked((int)Crc32(typed)))];
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>The JPEG with an APP1 Exif segment holding one IFD entry: Orientation = <paramref name="orientation"/>.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, // little-endian header, IFD at 8
            1, 0, // one entry
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, // 0x0112 SHORT x1 = orientation
            0, 0, 0, 0, // no next IFD
        ];
        byte[] exif = [.. "Exif\0\0"u8, .. tiff];
        var length = (ushort)(exif.Length + 2);
        byte[] app1 = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. exif];
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)];
    }
}

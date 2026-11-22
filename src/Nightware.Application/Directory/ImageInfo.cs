using System.Buffers.Binary;

namespace Nightware.Application.Directory;

/// <summary>The type and pixel size of a JPEG or PNG, read from its header (no decoding).</summary>
public sealed record ImageInfo(string MediaType, int Width, int Height)
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public bool IsSquare => Width == Height;

    /// <summary>The image's type and size; null when it is neither a JPEG nor a PNG, or its header is cut short.</summary>
    public static ImageInfo? Read(ReadOnlySpan<byte> image)
    {
        if (image.Length >= 24 && image[..8].SequenceEqual(PngSignature) && image.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return Sized(Png, BinaryPrimitives.ReadInt32BigEndian(image.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(image.Slice(20, 4)));
        }

        return image.Length >= 4 && image[0] == 0xFF && image[1] == 0xD8 ? ReadJpeg(image) : null;
    }

    /// <summary>Walks the JPEG's segments to the first start-of-frame marker (SOF0–SOF15, not DHT, JPG or DAC).</summary>
    private static ImageInfo? ReadJpeg(ReadOnlySpan<byte> image)
    {
        var offset = 2;
        while (offset + 4 <= image.Length)
        {
            if (image[offset] != 0xFF)
            {
                return null;
            }

            var marker = image[offset + 1];
            if (marker == 0xFF)
            {
                offset++; // fill byte
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2; // standalone markers carry no length
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset + 2, 2));
            if (length < 2)
            {
                return null;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return offset + 9 <= image.Length
                    ? Sized(Jpeg, BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset + 5, 2)))
                    : null;
            }

            if (marker == 0xDA)
            {
                return null; // scan data before any frame header
            }

            offset += 2 + length;
        }

        return null;
    }

    private static ImageInfo? Sized(string type, int width, int height) => width > 0 && height > 0 ? new ImageInfo(type, width, height) : null;
}

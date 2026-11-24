using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

using SkiaSharp;

namespace Nightware.Adapters.Dav.Directory;

/// <summary>
/// Makes a photo something Nextcloud keeps as an avatar: a square JPEG or PNG. A square JPEG or PNG passes untouched;
/// any other image SkiaSharp can read (JPEG, PNG, GIF, WebP, BMP, ICO; HEIF where the platform has a codec) is turned
/// upright by its EXIF orientation, centre-cropped to a square and re-encoded, at most <see cref="MaxEdge"/> pixels a side.
/// </summary>
/// <remarks>
/// The 4 MB upload cap doesn't bound the decoded size (a small file can claim a huge canvas), so the header's
/// dimensions are checked before anything is decoded (<see cref="MaxPixels"/>), and a large photo is decoded at the
/// smallest scale the codec offers that still covers <see cref="MaxEdge"/>.
/// </remarks>
internal static class SquarePhoto
{
    /// <summary>The largest avatar kept, per side (Nextcloud serves avatars up to 1024 px).</summary>
    public const int MaxEdge = 1024;

    /// <summary>The largest canvas decoded: 50 megapixels, a large phone camera's photo.</summary>
    public const long MaxPixels = 50_000_000;

    private const int JpegQuality = 90;

    public static Result<(ReadOnlyMemory<byte> Image, string ContentType)> From(ReadOnlyMemory<byte> image)
    {
        if (ImageInfo.Read(image.Span) is { IsSquare: true } info)
        {
            return (image, info.MediaType);
        }

        using var data = SKData.CreateCopy(image.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return Error.Validation("The photo is not an image the server can read.");
        }

        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            return Error.Validation($"The photo is too large to crop ({width}x{height}); the most is {MaxPixels / 1_000_000} megapixels.");
        }

        using var decoded = Decode(codec);
        if (decoded is null)
        {
            return Error.Validation("The photo could not be decoded.");
        }

        using var upright = Upright(decoded, codec.EncodedOrigin);
        var side = Math.Min(upright.Width, upright.Height);
        var edge = Math.Min(side, MaxEdge);
        var source = SKRectI.Create((upright.Width - side) / 2, (upright.Height - side) / 2, side, side);
        var opaque = codec.Info.AlphaType == SKAlphaType.Opaque;
        using var square = new SKBitmap(new SKImageInfo(edge, edge, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul));
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.Transparent);
            using var drawn = SKImage.FromBitmap(upright);
            canvas.DrawImage(drawn, source, SKRect.Create(edge, edge), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var encoded = square.Encode(opaque ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, JpegQuality);
        return encoded is null
            ? Error.Validation("The photo could not be re-encoded.")
            : (encoded.ToArray(), opaque ? ImageInfo.Jpeg : ImageInfo.Png);
    }

    /// <summary>
    /// The image, decoded at the smallest scale the codec supports whose shorter side still covers <see cref="MaxEdge"/>
    /// (JPEG decodes at 1/2, 1/4, 1/8 for nothing), else whole.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec)
    {
        var shorter = Math.Min(codec.Info.Width, codec.Info.Height);
        var scale = shorter > MaxEdge ? (float)MaxEdge / shorter : 1f;
        var size = codec.GetScaledDimensions(scale);
        if (Math.Min(size.Width, size.Height) < Math.Min(MaxEdge, shorter))
        {
            size = codec.Info.Size;
        }

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, codec.Info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return null;
    }

    /// <summary>The bitmap as it should be seen: EXIF's orientation (a phone's sideways photo) applied.</summary>
    private static SKBitmap Upright(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap.Copy();
        }

        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swaps ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        var turned = new SKBitmap(new SKImageInfo(w, h, bitmap.ColorType, bitmap.AlphaType));
        using var canvas = new SKCanvas(turned);
        canvas.SetMatrix(origin switch
        {
            SKEncodedOrigin.TopRight => SKMatrix.CreateScale(-1, 1, w / 2f, 0),
            SKEncodedOrigin.BottomRight => SKMatrix.CreateRotationDegrees(180, w / 2f, h / 2f),
            SKEncodedOrigin.BottomLeft => SKMatrix.CreateScale(1, -1, 0, h / 2f),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        });
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
        return turned;
    }
}

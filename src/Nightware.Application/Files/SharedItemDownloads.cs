using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>What may be downloaded from a shared item, and in which format — one rule for both legs of a download.</summary>
internal static class SharedItemDownloads
{
    /// <summary>Graph's code for a content request a driveItem cannot satisfy (a folder, an unsupported conversion).</summary>
    public const string NotSupportedCode = "notSupported";

    public static Result Check(SharedItem item, ContentFormat format, IDocumentConverter converter)
    {
        if (item.IsFolder)
        {
            return new Error(ErrorKind.Validation, NotSupportedCode, "A folder has no content to download.");
        }

        if (format == ContentFormat.Pdf && !item.IsPdf && !converter.CanConvertToPdf(item.Name, item.MimeType))
        {
            return new Error(ErrorKind.Validation, NotSupportedCode, $"'{item.Name}' cannot be converted to PDF.");
        }

        return Result.Success();
    }

    /// <summary>Whether <paramref name="format"/> calls for the converter (an item already in PDF is served as it is).</summary>
    public static bool NeedsConversion(SharedItem item, ContentFormat format) => format == ContentFormat.Pdf && !item.IsPdf;
}

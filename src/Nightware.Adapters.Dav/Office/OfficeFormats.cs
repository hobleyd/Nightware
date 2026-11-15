namespace Nightware.Adapters.Dav.Office;

/// <summary>
/// The documents offered as PDF: Office and OpenDocument formats, the set Graph converts for SharePoint and
/// OneDrive and NightMail previews as a server-rendered PDF. Collabora reads more (images, plain text), but
/// those the client draws itself, so converting them would only make the preview worse.
/// </summary>
internal static class OfficeFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "doc", "docm", "docx", "dot", "dotx", "odg", "odp", "ods", "odt", "pot", "potm", "potx",
        "pps", "ppsx", "ppt", "pptm", "pptx", "rtf", "xls", "xlsb", "xlsm", "xlsx",
    };

    private static readonly string[] MimePrefixes =
    [
        "application/vnd.openxmlformats-officedocument.",
        "application/vnd.oasis.opendocument.",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/msword",
    ];

    public static bool IsConvertible(string fileName, string? mimeType) =>
        Extensions.Contains(ExtensionOf(fileName))
        || (mimeType is not null && MimePrefixes.Any(p => mimeType.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The extension without its dot, or empty.</summary>
    public static string ExtensionOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? string.Empty : fileName[(dot + 1)..];
    }
}

using System.Globalization;
using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Files;

namespace Nightware.Adapters.Dav.Files;

/// <summary>A PROPFIND/SEARCH response for one file or folder as a Graph <c>driveItem</c>.</summary>
internal static class SharedItemMapping
{
    public static readonly XName FileId = DavNs.OwnCloud + "fileid";

    /// <summary>What every lookup asks for — exactly what <see cref="ToItem"/> reads.</summary>
    public static readonly IReadOnlyList<XName> Properties =
    [
        DavNames.DisplayName, DavNames.GetContentType, DavNames.GetContentLength, DavNames.ResourceType, FileId, DavNames.GetLastModified,
    ];

    /// <param name="fallbackId">The id when Nextcloud reports no <c>oc:fileid</c>.</param>
    public static SharedItem ToItem(DavResponse response, ShareLink link, string fallbackId)
    {
        var name = response.Text(DavNames.DisplayName) is { Length: > 0 } displayName ? displayName : LastSegment(response.Href);
        var size = long.TryParse(response.Text(DavNames.GetContentLength), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : 0;
        var isFolder = response.HasResourceType(DavNames.Collection);
        return new SharedItem(response.Text(FileId) is { Length: > 0 } fileId ? fileId : fallbackId, name, size)
        {
            IsFolder = isFolder,
            MimeType = isFolder ? null : response.Text(DavNames.GetContentType),
            WebUrl = link.Url.AbsoluteUri,
            LastModifiedDateTime = DateTimeOffset.TryParse(response.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                ? modified.ToUniversalTime()
                : null,
        };
    }

    /// <summary>The PROPFIND body for <see cref="Properties"/>.</summary>
    public static string PropFindBody() =>
        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(DavNs.Dav + "propfind",
                new XAttribute(XNamespace.Xmlns + "d", DavNs.Dav),
                new XAttribute(XNamespace.Xmlns + "oc", DavNs.OwnCloud),
                new XElement(DavNames.Prop, Properties.Select(static p => new XElement(p))))).ToString(SaveOptions.DisableFormatting);

    private static string LastSegment(string href) =>
        Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
}

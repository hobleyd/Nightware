using System.Globalization;
using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Files;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>An item's parent as Graph's <c>parentReference</c> needs it: its id and its path below the root.</summary>
internal sealed record DriveParent(string Id, string Path);

/// <summary>A WebDAV <c>PROPFIND</c>/<c>SEARCH</c> response for a file or folder in the caller's home, as a <see cref="DriveItem"/>.</summary>
internal static class DriveItemMapping
{
    /// <summary>Nextcloud's Files namespace (<c>nc:</c> in its own clients), not the calendar's <c>nextcloud.com</c> one.</summary>
    public static readonly XNamespace FilesNs = "http://nextcloud.org/ns";

    public static readonly XName FileId = DavNs.OwnCloud + "fileid";
    public static readonly XName Size = DavNs.OwnCloud + "size";
    public static readonly XName Permissions = DavNs.OwnCloud + "permissions";
    public static readonly XName ShareTypes = DavNs.OwnCloud + "share-types";
    public static readonly XName CreationTime = FilesNs + "creation_time";
    public static readonly XName HasPreview = FilesNs + "has-preview";
    public static readonly XName FolderCount = FilesNs + "contained-folder-count";
    public static readonly XName FileCount = FilesNs + "contained-file-count";
    public static readonly XName Checksums = DavNs.OwnCloud + "checksums";
    public static readonly XName QuotaUsed = DavNs.Dav + "quota-used-bytes";
    public static readonly XName QuotaAvailable = DavNs.Dav + "quota-available-bytes";

    /// <summary>What every lookup asks for — exactly what <see cref="ToItem"/> reads.</summary>
    public static readonly IReadOnlyList<XName> Properties =
    [
        DavNames.DisplayName, DavNames.GetETag, FileId, Size, DavNames.GetContentLength, DavNames.GetLastModified, CreationTime,
        DavNames.ResourceType, DavNames.GetContentType, HasPreview, ShareTypes, Permissions, FolderCount, FileCount, Checksums,
    ];

    /// <param name="response">The item's response; its href must lie in <paramref name="home"/>.</param>
    /// <param name="relativeHref">The response's href relative to the DAV root.</param>
    /// <param name="parent">The item's parent; null for the home itself.</param>
    public static DriveItem ToItem(DavResponse response, string relativeHref, DriveHome home, DriveParent? parent)
    {
        var isRoot = relativeHref.TrimEnd('/') == home.Href.TrimEnd('/');
        var isFolder = response.HasResourceType(DavNames.Collection);
        var name = isRoot ? "root" : LastSegment(relativeHref);
        var etag = response.ETag?.Trim('"');
        var size = Long(response.Text(Size)) ?? Long(response.Text(DavNames.GetContentLength)) ?? 0;
        var created = Long(response.Text(CreationTime)) is > 0 and var seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
        var modified = DateTimeOffset.TryParse(response.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
            ? m.ToUniversalTime()
            : (DateTimeOffset?)null;
        var shareTypes = response.Find(ShareTypes)?.Elements().Any() == true;
        var sharedWithMe = response.Text(Permissions)?.Contains('S', StringComparison.Ordinal) == true;
        var checksums = isFolder ? null : response.Find(Checksums)?.Value;
        return new DriveItem(response.Text(FileId) ?? string.Empty, name)
        {
            DriveId = home.DriveId,
            Size = size,
            IsFolder = isFolder || isRoot,
            IsRoot = isRoot,
            MimeType = isFolder ? null : response.Text(DavNames.GetContentType),
            ETag = etag,
            CTag = isFolder ? null : etag,
            CreatedDateTime = created ?? modified,
            LastModifiedDateTime = modified,
            ParentId = parent?.Id,
            ParentPath = parent?.Path,
            ChildCount = isFolder || isRoot ? (int)((Long(response.Text(FolderCount)) ?? 0) + (Long(response.Text(FileCount)) ?? 0)) : null,
            HasThumbnail = !isFolder && string.Equals(response.Text(HasPreview), "true", StringComparison.OrdinalIgnoreCase),
            IsShared = shareTypes || sharedWithMe,
            Sha1Hash = Checksum(checksums, "SHA1", 40),
            Sha256Hash = Checksum(checksums, "SHA256", 64),
        };
    }

    /// <summary>
    /// One algorithm's value from Nextcloud's <c>oc:checksums</c> (<c>SHA1:abc… MD5:def…</c>, set when the uploader sent
    /// <c>OC-Checksum</c>), as upper-case hex as Graph writes it; null when absent or malformed.
    /// </summary>
    internal static string? Checksum(string? checksums, string algorithm, int hexLength)
    {
        if (string.IsNullOrWhiteSpace(checksums))
        {
            return null;
        }

        var prefix = algorithm + ":";
        foreach (var token in checksums.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var hex = token[prefix.Length..];
                return hex.Length == hexLength && hex.All(char.IsAsciiHexDigit) ? hex.ToUpperInvariant() : null;
            }
        }

        return null;
    }

    /// <summary>The PROPFIND body for <see cref="Properties"/> (and, with <paramref name="quota"/>, the quota).</summary>
    public static XDocument PropFind(bool quota = false) =>
        new(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(DavNs.Dav + "propfind",
                new XAttribute(XNamespace.Xmlns + "d", DavNs.Dav),
                new XAttribute(XNamespace.Xmlns + "oc", DavNs.OwnCloud),
                new XAttribute(XNamespace.Xmlns + "nc", FilesNs),
                new XElement(DavNames.Prop, (quota ? Properties.Concat([QuotaUsed, QuotaAvailable]) : Properties).Select(static p => new XElement(p)))));

    public static string LastSegment(string href) =>
        Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);

    /// <summary>The href of the folder holding <paramref name="href"/> (with a trailing slash).</summary>
    public static string ParentHref(string href)
    {
        var trimmed = href.TrimEnd('/');
        return trimmed[..(trimmed.LastIndexOf('/') + 1)];
    }

    public static long? Long(string? text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;
}

using System.Globalization;
using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>An item found in the caller's home: its href (relative to the DAV root; a folder's ends in <c>/</c>) and its properties.</summary>
internal sealed record LocatedItem(string Href, DavResponse Response)
{
    public bool IsFolder => Href.EndsWith('/');

    public string? FileId => Response.Text(DriveItemMapping.FileId);

    public string? ETag => Response.ETag?.Trim('"');
}

/// <summary>
/// Turns <see cref="DriveItemRef"/>s into hrefs in the caller's home and DAV responses into <see cref="DriveItem"/>s with
/// their parents (ADR 0008 D2). One per request scope: it remembers the ids it has seen, so a parent is looked up once.
/// </summary>
internal sealed class DriveItemLocator(IDavClient dav, DavPaths paths, DriveHomeLocator homes)
{
    /// <summary>Most parents looked up at once when mapping a page of items.</summary>
    private const int ParallelLookups = 8;

    private static readonly XNamespace SearchDav = "https://github.com/icewind1991/SearchDAV/ns";

    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    public Task<Result<DriveHome>> HomeAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return homes.ForDriveAsync(item.DriveId, cancellationToken);
    }

    /// <summary>The item <paramref name="item"/> names; NotFound when it doesn't exist (or lies outside the home).</summary>
    public async Task<Result<LocatedItem>> LocateAsync(DriveHome home, DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var anchor = await AnchorAsync(home, item.ItemId, cancellationToken).ConfigureAwait(false);
        if (!anchor.IsSuccess || item.Path.Count == 0)
        {
            return anchor;
        }

        if (!anchor.Value.IsFolder)
        {
            return Error.NotFound("A path can only be followed below a folder.");
        }

        return await PropFindAsync(home, Below(anchor.Value.Href, item.Path), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where a write to <paramref name="item"/> lands: the href, and the item already there (null for a free path). The
    /// anchor must exist; the last path segment need not.
    /// </summary>
    public async Task<Result<(string Href, LocatedItem? Existing)>> TargetAsync(DriveHome home, DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Path.Count == 0)
        {
            var located = await AnchorAsync(home, item.ItemId, cancellationToken).ConfigureAwait(false);
            return located.IsSuccess ? (located.Value.Href, located.Value) : located.Error;
        }

        var anchor = await AnchorAsync(home, item.ItemId, cancellationToken).ConfigureAwait(false);
        if (!anchor.IsSuccess)
        {
            return anchor.Error;
        }

        if (!anchor.Value.IsFolder)
        {
            return Error.NotFound("A path can only be followed below a folder.");
        }

        var href = Below(anchor.Value.Href, item.Path);
        var existing = await PropFindAsync(home, href, cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccess)
        {
            return (existing.Value.Href, existing.Value);
        }

        return existing.Error.Kind == ErrorKind.NotFound ? (href, null) : existing.Error;
    }

    /// <summary>The folder <paramref name="folder"/> names; a file is <see cref="ErrorKind.Validation"/>.</summary>
    public async Task<Result<LocatedItem>> FolderAsync(DriveHome home, DriveItemRef folder, CancellationToken cancellationToken)
    {
        var located = await LocateAsync(home, folder, cancellationToken).ConfigureAwait(false);
        return !located.IsSuccess || located.Value.IsFolder ? located : Error.Validation("The parent is a file, not a folder.");
    }

    public async Task<Result<LocatedItem>> PropFindAsync(DriveHome home, string href, CancellationToken cancellationToken)
    {
        var found = await dav.PropFindAsync(href, DavDepth.Zero, DriveItemMapping.Properties, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        return found.Value.Responses.FirstOrDefault(static r => r.IsFound) is { } response && Located(home, response) is { } located
            ? located
            : Error.NotFound("The item does not exist.");
    }

    /// <summary>The folder's own entry and its children (Depth 1).</summary>
    public async Task<Result<IReadOnlyList<LocatedItem>>> ChildrenAsync(DriveHome home, LocatedItem folder, CancellationToken cancellationToken)
    {
        var listed = await dav.PropFindAsync(folder.Href, DavDepth.One, DriveItemMapping.Properties, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        Remember(folder);
        IReadOnlyList<LocatedItem> children = listed.Value.Responses
            .Where(static r => r.IsFound)
            .Select(r => Located(home, r))
            .OfType<LocatedItem>()
            .Where(c => c.Href.TrimEnd('/') != folder.Href.TrimEnd('/'))
            .ToList();
        return Result<IReadOnlyList<LocatedItem>>.Success(children);
    }

    /// <summary>A <c>SEARCH</c> in the home. Results outside the home are dropped.</summary>
    public async Task<Result<IReadOnlyList<LocatedItem>>> SearchAsync(
        DriveHome home, XElement where, int? limit, int offset, XElement? orderBy, CancellationToken cancellationToken)
    {
        var body = SearchBody(home, where, limit, offset, orderBy);
        var found = await dav.SearchAsync(body, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        IReadOnlyList<LocatedItem> items = found.Value.Responses.Where(static r => r.IsFound).Select(r => Located(home, r)).OfType<LocatedItem>().ToList();
        return Result<IReadOnlyList<LocatedItem>>.Success(items);
    }

    /// <summary>Items by file id (several at once); ids that match nothing are simply absent.</summary>
    public async Task<Result<IReadOnlyList<LocatedItem>>> ByIdsAsync(DriveHome home, IReadOnlyCollection<long> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return Result<IReadOnlyList<LocatedItem>>.Success([]);
        }

        var clauses = fileIds.Select(static id => new XElement(DavNs.Dav + "eq",
            new XElement(DavNames.Prop, new XElement(DriveItemMapping.FileId)),
            new XElement(DavNs.Dav + "literal", id.ToString(CultureInfo.InvariantCulture)))).ToList();
        var where = clauses.Count == 1 ? clauses[0] : new XElement(DavNs.Dav + "or", clauses);
        return await SearchAsync(home, where, fileIds.Count, 0, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The item as a <see cref="DriveItem"/>, with its parent.</summary>
    public async Task<Result<DriveItem>> ToItemAsync(DriveHome home, LocatedItem item, CancellationToken cancellationToken)
    {
        var items = await ToItemsAsync(home, [item], cancellationToken).ConfigureAwait(false);
        return items.IsSuccess ? items.Value[0] : items.Error;
    }

    /// <summary>The items as <see cref="DriveItem"/>s, in order; each distinct parent is looked up once.</summary>
    public async Task<Result<IReadOnlyList<DriveItem>>> ToItemsAsync(DriveHome home, IReadOnlyList<LocatedItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            Remember(item);
        }

        var parentHrefs = items.Where(i => !IsHome(home, i.Href)).Select(static i => DriveItemMapping.ParentHref(i.Href)).Distinct(StringComparer.Ordinal).ToList();
        var parents = new Dictionary<string, DriveParent>(StringComparer.Ordinal);
        foreach (var batch in parentHrefs.Chunk(ParallelLookups))
        {
            var resolved = await Task.WhenAll(batch.Select(h => ParentAsync(home, h, cancellationToken))).ConfigureAwait(false);
            for (var i = 0; i < batch.Length; i++)
            {
                var parent = resolved[i];
                if (!parent.IsSuccess)
                {
                    return parent.Error;
                }

                parents[batch[i]] = parent.Value;
            }
        }

        IReadOnlyList<DriveItem> mapped = items
            .Select(i => DriveItemMapping.ToItem(i.Response, i.Href, home, IsHome(home, i.Href) ? null : parents[DriveItemMapping.ParentHref(i.Href)]))
            .ToList();
        return Result<IReadOnlyList<DriveItem>>.Success(mapped);
    }

    /// <summary>The names directly in a folder (for picking a free name), compared as Nextcloud does: case-insensitively.</summary>
    public async Task<Result<HashSet<string>>> NamesInAsync(DriveHome home, string folderHref, CancellationToken cancellationToken)
    {
        var listed = await dav.PropFindAsync(folderHref, DavDepth.One, [DavNames.ResourceType], cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        return listed.Value.Responses
            .Select(r => paths.ToRelative(r.Href))
            .OfType<string>()
            .Where(h => h.TrimEnd('/') != folderHref.TrimEnd('/'))
            .Select(DriveItemMapping.LastSegment)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>OneDrive's free name for a conflict: <c>name 1.ext</c>, <c>name 2.ext</c>, …</summary>
    public static string FreeName(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = extension.Length is > 0 and < 16 && extension.Length < name.Length ? name[..^extension.Length] : name;
        extension = stem.Length == name.Length ? string.Empty : extension;
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem} {n.ToString(CultureInfo.InvariantCulture)}{extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>The href of <paramref name="name"/> in the folder <paramref name="folderHref"/> (a folder's with a trailing slash).</summary>
    public static string Child(string folderHref, string name, bool isFolder = false) =>
        folderHref + Uri.EscapeDataString(name) + (isFolder ? "/" : string.Empty);

    public static bool IsHome(DriveHome home, string href) => href.TrimEnd('/') == home.Href.TrimEnd('/');

    public void Remember(LocatedItem item)
    {
        if (item.FileId is { } id)
        {
            _ids[item.Href.TrimEnd('/')] = id;
        }
    }

    private async Task<Result<LocatedItem>> AnchorAsync(DriveHome home, string? itemId, CancellationToken cancellationToken)
    {
        if (itemId is null)
        {
            return await PropFindAsync(home, home.Href, cancellationToken).ConfigureAwait(false);
        }

        if (!long.TryParse(itemId, NumberStyles.None, CultureInfo.InvariantCulture, out var fileId) || fileId <= 0)
        {
            return Error.NotFound("The item does not exist.");
        }

        var found = await ByIdsAsync(home, [fileId], cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        // The home itself never comes back from a SEARCH; its id is still a valid anchor.
        if (found.Value.FirstOrDefault(i => i.FileId == itemId) is { } item)
        {
            return item;
        }

        var root = await PropFindAsync(home, home.Href, cancellationToken).ConfigureAwait(false);
        return root.IsSuccess && root.Value.FileId == itemId ? root : Error.NotFound("The item does not exist.");
    }

    private async Task<Result<DriveParent>> ParentAsync(DriveHome home, string parentHref, CancellationToken cancellationToken)
    {
        var path = home.PathOf(parentHref) ?? "/";
        if (_ids.TryGetValue(parentHref.TrimEnd('/'), out var known))
        {
            return new DriveParent(known, path);
        }

        var parent = await PropFindAsync(home, parentHref, cancellationToken).ConfigureAwait(false);
        if (!parent.IsSuccess)
        {
            return parent.Error;
        }

        Remember(parent.Value);
        return new DriveParent(parent.Value.FileId ?? string.Empty, path);
    }

    private LocatedItem? Located(DriveHome home, DavResponse response)
    {
        var relative = paths.ToRelative(response.Href);
        if (relative is null || home.PathOf(relative) is null)
        {
            return null;
        }

        var isFolder = response.HasResourceType(DavNames.Collection);
        var href = isFolder ? relative.TrimEnd('/') + "/" : relative.TrimEnd('/');
        return new LocatedItem(href, response);
    }

    private static string Below(string folderHref, IReadOnlyList<string> segments) =>
        folderHref + string.Join('/', segments.Select(Uri.EscapeDataString));

    private static XDocument SearchBody(DriveHome home, XElement where, int? limit, int offset, XElement? orderBy)
    {
        var basic = new XElement(DavNs.Dav + "basicsearch",
            new XElement(DavNs.Dav + "select", new XElement(DavNames.Prop, DriveItemMapping.Properties.Select(static p => new XElement(p)))),
            new XElement(DavNs.Dav + "from",
                new XElement(DavNs.Dav + "scope",
                    new XElement(DavNames.Href, "/" + home.Href.TrimEnd('/')),
                    new XElement(DavNs.Dav + "depth", "infinity"))),
            new XElement(DavNs.Dav + "where", where),
            new XElement(DavNs.Dav + "orderby", orderBy));
        if (limit is { } n)
        {
            basic.Add(new XElement(DavNs.Dav + "limit",
                new XElement(DavNs.Dav + "nresults", n.ToString(CultureInfo.InvariantCulture)),
                new XElement(SearchDav + "firstresult", offset.ToString(CultureInfo.InvariantCulture))));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(DavNs.Dav + "searchrequest",
                new XAttribute(XNamespace.Xmlns + "d", DavNs.Dav),
                new XAttribute(XNamespace.Xmlns + "oc", DavNs.OwnCloud),
                new XAttribute(XNamespace.Xmlns + "nc", DriveItemMapping.FilesNs),
                new XAttribute(XNamespace.Xmlns + "ns", SearchDav),
                basic));
    }

    /// <summary>An <c>orderby</c> on <paramref name="key"/>.</summary>
    public static XElement OrderBy(DriveSortKey key, bool descending) =>
        new(DavNs.Dav + "order",
            new XElement(DavNames.Prop, new XElement(key switch
            {
                DriveSortKey.LastModifiedDateTime => DavNames.GetLastModified,
                DriveSortKey.Size => DriveItemMapping.Size,
                _ => DavNames.DisplayName,
            })),
            new XElement(DavNs.Dav + (descending ? "descending" : "ascending")));

    /// <summary><c>NOT is-collection</c>: files only.</summary>
    public static XElement NotFolder() =>
        new(DavNs.Dav + "not", new XElement(DavNs.Dav + "is-collection"));

    /// <summary><c>displayname LIKE pattern</c>.</summary>
    public static XElement NameLike(string pattern) =>
        new(DavNs.Dav + "like", new XElement(DavNames.Prop, new XElement(DavNames.DisplayName)), new XElement(DavNs.Dav + "literal", pattern));
}

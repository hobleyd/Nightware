using System.Globalization;
using System.Net;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// <see cref="IDriveStore"/> over the caller's Nextcloud home (ADR 0008): WebDAV for items and content, <c>SEARCH</c> for ids
/// and names, Nextcloud's preview renderer for thumbnails. Everything runs as the caller.
/// </summary>
internal sealed class NextcloudDriveStore(IDavClient dav, DriveItemLocator locator, DriveHomeLocator homes, NextcloudAppRequests app) : IDriveStore
{
    public async Task<Result<Domain.Files.Drive>> GetDriveAsync(string? driveId, CancellationToken cancellationToken) =>
        await DriveAsync(await homes.ForDriveAsync(driveId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<Result<Domain.Files.Drive>> GetDriveOfAsync(string owner, string? driveId, CancellationToken cancellationToken) =>
        await DriveAsync(await homes.ForOwnerAsync(owner, driveId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<Result<Domain.Files.Drive>> DriveAsync(Result<DriveHome> home, CancellationToken cancellationToken)
    {
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var listed = await dav.PropFindAsync(home.Value.Href, DavDepth.Zero, [.. DriveItemMapping.Properties, DriveItemMapping.QuotaUsed, DriveItemMapping.QuotaAvailable], cancellationToken)
            .ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var root = listed.Value.Responses.FirstOrDefault(static r => r.IsFound);
        var used = DriveItemMapping.Long(root?.Text(DriveItemMapping.QuotaUsed)) ?? 0;
        var available = DriveItemMapping.Long(root?.Text(DriveItemMapping.QuotaAvailable));

        // Nextcloud reports "unlimited" (and "not computed") as negative values: no total, nothing remaining to report.
        var remaining = available is >= 0 ? available : null;
        var uid = home.Value.OwnerUid ?? Uri.UnescapeDataString(home.Value.Uid);
        return new Domain.Files.Drive(home.Value.DriveId, home.Value.IsShared ? root?.Text(DavNames.DisplayName) ?? "Files" : "OneDrive")
        {
            IsShared = home.Value.IsShared,
            OwnerId = uid,
            OwnerDisplayName = root?.Text(DavNames.DisplayName) ?? uid,
            LastModifiedDateTime = DateTimeOffset.TryParse(root?.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                ? modified.ToUniversalTime()
                : null,
            Quota = new DriveQuota(used, remaining, remaining is { } left ? used + left : null),
        };
    }

    public async Task<Result<DriveItem>> GetItemAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        return located.IsSuccess ? await locator.ToItemAsync(home.Value, located.Value, cancellationToken).ConfigureAwait(false) : located.Error;
    }

    public async Task<Result<Page<DriveItem>>> ListChildrenAsync(DriveItemRef folder, DriveListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var home = await locator.HomeAsync(folder, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.FolderAsync(home.Value, folder, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var children = await locator.ChildrenAsync(home.Value, located.Value, cancellationToken).ConfigureAwait(false);
        if (!children.IsSuccess)
        {
            return children.Error;
        }

        var mapped = await locator.ToItemsAsync(home.Value, children.Value, cancellationToken).ConfigureAwait(false);
        if (!mapped.IsSuccess)
        {
            return mapped.Error;
        }

        if (!TryOffset(query.SkipToken, out var offset))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        var sorted = Sort(mapped.Value, query).ToList();
        var page = sorted.Skip(offset).Take(query.Top).ToList();
        var next = offset + page.Count < sorted.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<DriveItem>(page, next);
    }

    public async Task<Result<BlobContent>> OpenContentAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (located.Value.IsFolder)
        {
            return DriveErrors.NotSupported("A folder has no content to download.");
        }

        var download = await dav.OpenReadAsync(located.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return download.Error;
        }

        Stream body = new ResponseStream(download.Value.Content, download.Value);
        var type = located.Value.Response.Text(DavNames.GetContentType) ?? download.Value.ContentType;
        return new BlobContent(type, download.Value.Length, _ => Task.FromResult(body)) { FileName = DriveItemMapping.LastSegment(located.Value.Href) };
    }

    public async Task<Result<DriveItem>> WriteContentAsync(DriveItemRef target, DriveUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(upload);
        var home = await locator.HomeAsync(target, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var resolved = await locator.TargetAsync(home.Value, target, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var (href, existing) = resolved.Value;
        if (existing is { IsFolder: true })
        {
            return Error.Conflict("A folder of that name is already there.");
        }

        if (upload.IfMatch is { } ifMatch && (existing is null || !ETagMatches(ifMatch, existing.ETag)))
        {
            return DavErrors.PreconditionFailed("The item was changed by someone else (eTag mismatch).");
        }

        var precondition = DavPrecondition.None;
        if (existing is not null && target.Path.Count > 0)
        {
            switch (upload.Conflict)
            {
                case ConflictBehavior.Fail:
                    return NameTaken();
                case ConflictBehavior.Rename:
                    var parentHref = DriveItemMapping.ParentHref(href);
                    var taken = await locator.NamesInAsync(home.Value, parentHref, cancellationToken).ConfigureAwait(false);
                    if (!taken.IsSuccess)
                    {
                        return taken.Error;
                    }

                    href = DriveItemLocator.Child(parentHref, DriveItemLocator.FreeName(DriveItemMapping.LastSegment(href), taken.Value));
                    precondition = DavPrecondition.CreateOnly;
                    break;
            }
        }
        else if (existing is null)
        {
            precondition = DavPrecondition.CreateOnly;
        }

        var counted = new CountingReadStream(upload.Content, upload.Length);
        var put = await dav.PutAsync(href, counted, ContentType(upload.ContentType), precondition, cancellationToken).ConfigureAwait(false);
        if (!put.IsSuccess)
        {
            return put.Error.Kind == ErrorKind.Conflict && put.Error.Code != DavErrors.ItemExistsCode
                ? Error.NotFound("The parent folder does not exist.")
                : put.Error;
        }

        if (upload.Length is { } expected && (counted.Exceeded || counted.BytesRead != expected))
        {
            // The body disagreed with its Content-Length: a half-written file must not stay. (Best effort.)
            _ = await dav.DeleteAsync(href, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
            return Error.Validation($"The body holds {counted.BytesRead} bytes, but Content-Length says {expected}.", "Content-Length");
        }

        return await ItemAtAsync(home.Value, href, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DriveItem>> CreateFolderAsync(DriveItemRef parent, string name, ConflictBehavior conflict, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(parent, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var folder = await locator.FolderAsync(home.Value, parent, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        var taken = await locator.NamesInAsync(home.Value, folder.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!taken.IsSuccess)
        {
            return taken.Error;
        }

        var finalName = name;
        if (taken.Value.Contains(name))
        {
            switch (conflict)
            {
                case ConflictBehavior.Fail:
                    return NameTaken();
                case ConflictBehavior.Rename:
                    finalName = DriveItemLocator.FreeName(name, taken.Value);
                    break;
                default:
                    var existing = DriveItemLocator.Child(folder.Value.Href, name);
                    var deleted = await dav.DeleteAsync(existing, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
                    if (!deleted.IsSuccess)
                    {
                        return deleted.Error;
                    }

                    break;
            }
        }

        var href = DriveItemLocator.Child(folder.Value.Href, finalName, isFolder: true);
        var made = await dav.MkColAsync(href, null, cancellationToken).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error.Kind == ErrorKind.Conflict ? NameTaken() : made.Error;
        }

        return await ItemAtAsync(home.Value, href, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DriveItem>> UpdateAsync(DriveItemRef item, DriveItemChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (change.IfMatch is { } ifMatch && !ETagMatches(ifMatch, located.Value.ETag))
        {
            return DavErrors.PreconditionFailed("The item was changed by someone else (eTag mismatch).");
        }

        var destinationFolder = DriveItemMapping.ParentHref(located.Value.Href);
        if (change.NewParent is { } newParent)
        {
            var folder = await locator.FolderAsync(home.Value, newParent with { DriveId = item.DriveId }, cancellationToken).ConfigureAwait(false);
            if (!folder.IsSuccess)
            {
                return folder.Error;
            }

            if (located.Value.IsFolder && folder.Value.Href.StartsWith(located.Value.Href, StringComparison.Ordinal))
            {
                return Error.Validation("A folder can't be moved into itself.");
            }

            destinationFolder = folder.Value.Href;
        }

        var name = change.Name ?? DriveItemMapping.LastSegment(located.Value.Href);
        var moved = await MoveOrCopyAsync(home.Value, located.Value, destinationFolder, name, change.Conflict, copy: false, cancellationToken).ConfigureAwait(false);
        return moved.IsSuccess ? await ItemAtAsync(home.Value, moved.Value, cancellationToken).ConfigureAwait(false) : moved.Error;
    }

    public async Task<Result> DeleteAsync(DriveItemRef item, string? ifMatch, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (DriveItemLocator.IsHome(home.Value, located.Value.Href))
        {
            return Error.Forbidden("The root folder can't be deleted.");
        }

        if (ifMatch is not null && !ETagMatches(ifMatch, located.Value.ETag))
        {
            return DavErrors.PreconditionFailed("The item was changed by someone else (eTag mismatch).");
        }

        return await dav.DeleteAsync(located.Value.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DriveItem>> CopyAsync(DriveItemRef item, DriveItemRef? parent, string? name, ConflictBehavior conflict, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var destinationFolder = DriveItemMapping.ParentHref(located.Value.Href);
        if (parent is not null)
        {
            var folder = await locator.FolderAsync(home.Value, parent with { DriveId = item.DriveId }, cancellationToken).ConfigureAwait(false);
            if (!folder.IsSuccess)
            {
                return folder.Error;
            }

            if (located.Value.IsFolder && folder.Value.Href.StartsWith(located.Value.Href, StringComparison.Ordinal))
            {
                return Error.Validation("A folder can't be copied into itself.");
            }

            destinationFolder = folder.Value.Href;
        }

        var copied = await MoveOrCopyAsync(home.Value, located.Value, destinationFolder, name ?? DriveItemMapping.LastSegment(located.Value.Href), conflict, copy: true, cancellationToken)
            .ConfigureAwait(false);
        return copied.IsSuccess ? await ItemAtAsync(home.Value, copied.Value, cancellationToken).ConfigureAwait(false) : copied.Error;
    }

    public async Task<Result<Page<DriveItem>>> SearchAsync(string text, DriveListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        if (!TryOffset(query.SkipToken, out var offset))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        // LIKE wildcards in the text are matched literally by dropping them: a search for "50%" finds "50".
        var pattern = "%" + text.Replace("%", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal) + "%";
        var found = await locator.SearchAsync(
            home.Value, DriveItemLocator.NameLike(pattern), query.Top + 1, offset, DriveItemLocator.OrderBy(query.Sort, query.Descending), cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var page = found.Value.Take(query.Top).ToList();
        var mapped = await locator.ToItemsAsync(home.Value, page, cancellationToken).ConfigureAwait(false);
        if (!mapped.IsSuccess)
        {
            return mapped.Error;
        }

        var next = found.Value.Count > query.Top ? (offset + query.Top).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<DriveItem>(mapped.Value, next);
    }

    public async Task<Result<IReadOnlyList<DriveItem>>> RecentAsync(int top, CancellationToken cancellationToken)
    {
        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        // As Nextcloud's own "Recent" view: files only (no collections), by modification time, newest first.
        var found = await locator.SearchAsync(
            home.Value, DriveItemLocator.NotFolder(), top, 0, DriveItemLocator.OrderBy(DriveSortKey.LastModifiedDateTime, descending: true), cancellationToken)
            .ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        return await locator.ToItemsAsync(home.Value, found.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DriveItem>> GetSpecialFolderAsync(SpecialFolder folder, string? appName, CancellationToken cancellationToken)
    {
        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        string[] segments = folder switch
        {
            SpecialFolder.Documents => ["Documents"],
            SpecialFolder.Photos => ["Photos"],
            SpecialFolder.CameraRoll => ["Photos", "Camera Roll"],
            SpecialFolder.Music => ["Music"],
            _ => ["Apps", appName ?? throw new ArgumentNullException(nameof(appName))],
        };

        var href = home.Value.Href;
        foreach (var segment in segments)
        {
            href = DriveItemLocator.Child(href, segment, isFolder: true);
            var made = await dav.MkColAsync(href, null, cancellationToken).ConfigureAwait(false);
            if (!made.IsSuccess && made.Error.Kind != ErrorKind.Conflict)
            {
                return made.Error;
            }
        }

        return await ItemAtAsync(home.Value, href, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<BlobContent>> OpenThumbnailAsync(DriveItemRef item, ThumbnailSize size, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (located.Value.IsFolder || !string.Equals(located.Value.Response.Text(DriveItemMapping.HasPreview), "true", StringComparison.OrdinalIgnoreCase)
            || located.Value.FileId is not { } fileId)
        {
            return Error.NotFound("The item has no thumbnail.");
        }

        var edge = ThumbnailSizes.EdgeOf(size).ToString(CultureInfo.InvariantCulture);
        var sent = await app.GetAsync($"index.php/core/preview?fileId={Uri.EscapeDataString(fileId)}&x={edge}&y={edge}&a=1", ocs: false, cancellationToken)
            .ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            using (response)
            {
                return response.StatusCode == HttpStatusCode.NotFound
                    ? Error.NotFound("The item has no thumbnail.")
                    : DavErrors.Map(response.StatusCode, null, null);
            }
        }

        var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        Stream body = new ResponseStream(content, response);
        return new BlobContent(response.Content.Headers.ContentType?.MediaType ?? "image/png", response.Content.Headers.ContentLength, _ => Task.FromResult(body));
    }

    /// <summary>Moves or copies <paramref name="item"/> to <paramref name="name"/> in <paramref name="folderHref"/>, honouring <paramref name="conflict"/>.</summary>
    private async Task<Result<string>> MoveOrCopyAsync(
        DriveHome home, LocatedItem item, string folderHref, string name, ConflictBehavior conflict, bool copy, CancellationToken cancellationToken)
    {
        var destination = DriveItemLocator.Child(folderHref, name, item.IsFolder);
        if (destination.TrimEnd('/') == item.Href.TrimEnd('/'))
        {
            if (!copy)
            {
                return destination; // Nothing to do: same name, same folder.
            }

            conflict = conflict == ConflictBehavior.Replace ? ConflictBehavior.Rename : conflict;
        }

        var overwrite = conflict == ConflictBehavior.Replace;
        if (conflict == ConflictBehavior.Rename)
        {
            var taken = await locator.NamesInAsync(home, folderHref, cancellationToken).ConfigureAwait(false);
            if (!taken.IsSuccess)
            {
                return taken.Error;
            }

            destination = DriveItemLocator.Child(folderHref, DriveItemLocator.FreeName(name, taken.Value), item.IsFolder);
        }

        var done = copy
            ? await dav.CopyAsync(item.Href, destination, overwrite, cancellationToken).ConfigureAwait(false)
            : await dav.MoveAsync(item.Href, destination, overwrite, cancellationToken).ConfigureAwait(false);
        if (!done.IsSuccess)
        {
            return done.Error.Kind == ErrorKind.Conflict ? NameTaken() : done.Error;
        }

        return destination;
    }

    private async Task<Result<DriveItem>> ItemAtAsync(DriveHome home, string href, CancellationToken cancellationToken)
    {
        var located = await locator.PropFindAsync(home, href, cancellationToken).ConfigureAwait(false);
        return located.IsSuccess ? await locator.ToItemAsync(home, located.Value, cancellationToken).ConfigureAwait(false) : located.Error;
    }

    private static IEnumerable<DriveItem> Sort(IEnumerable<DriveItem> items, DriveListQuery query)
    {
        Func<DriveItem, object?> key = query.Sort switch
        {
            DriveSortKey.LastModifiedDateTime => static i => i.LastModifiedDateTime,
            DriveSortKey.Size => static i => i.Size,
            _ => static i => i.Name,
        };
        var comparer = query.Sort == DriveSortKey.Name ? (IComparer<object?>)new NameComparer() : Comparer<object?>.Default;
        return query.Descending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);
    }

    private static bool TryOffset(string? skipToken, out int offset)
    {
        offset = 0;
        return skipToken is null || (int.TryParse(skipToken, NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0);
    }

    private static bool ETagMatches(string ifMatch, string? etag) =>
        ifMatch.Trim() == "*" || (etag is not null && string.Equals(ifMatch.Trim().Trim('"').Replace("W/", string.Empty, StringComparison.Ordinal).Trim('"'), etag, StringComparison.Ordinal));

    private static string ContentType(string? type) => string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type;

    private static Error NameTaken() =>
        new(ErrorKind.Conflict, DriveErrors.NameAlreadyExistsCode, "An item with that name already exists.");

    private sealed class NameComparer : IComparer<object?>
    {
        public int Compare(object? x, object? y) => string.Compare(x as string, y as string, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tasks.Attachments;

/// <summary>
/// Where task attachments live: the mailbox owner's Nextcloud Files, at
/// <c>Nightware/Task attachments/{uuid}/{file name}</c>. The VTODO references the file with an
/// <c>ATTACH</c> URL on the configured Nextcloud origin; reading it back, only the path counts, so
/// the reference survives a change of host name.
/// </summary>
internal sealed class TaskAttachmentFiles(IDavClient dav, DavPaths paths)
{
    public static readonly string[] Folder = ["Nightware", "Task attachments"];
    private const string FilesRoot = "files/";
    private const int MaxSegmentLength = 200;

    /// <summary>The principal's Files home (<c>files/{uid}/</c>); the uid comes from the discovered principal, never the address.</summary>
    public Result<string> HomeOf(DavPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var segments = paths.ToRelative(principal.Href)?.TrimEnd('/').Split('/');
        return segments is ["principals", "users", { Length: > 0 } uid]
            ? $"{FilesRoot}{uid}/"
            : Error.Unsupported("Only user mailboxes can hold task attachments.");
    }

    /// <summary>
    /// Streams <paramref name="content"/> into a new file. With <paramref name="expectedSize"/>, a body
    /// of any other length is refused and nothing is kept.
    /// </summary>
    /// <returns>The file's href (relative to the DAV root) and its length.</returns>
    public Task<Result<(string Href, long Size)>> StoreAsync(
        string home, string name, string contentType, Stream content, long? expectedSize, CancellationToken cancellationToken) =>
        StoreAsync(home, Folder, name, contentType, content, expectedSize, cancellationToken);

    /// <summary>As above, under <paramref name="folderPath"/> (e.g. event attachments' own folder).</summary>
    public async Task<Result<(string Href, long Size)>> StoreAsync(
        string home, IReadOnlyList<string> folderPath, string name, string contentType, Stream content, long? expectedSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        var folder = home;
        foreach (var segment in folderPath)
        {
            folder += Uri.EscapeDataString(segment) + "/";
            var created = await dav.MkColAsync(folder, null, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess && created.Error.Kind != ErrorKind.Conflict)
            {
                return created.Error;
            }
        }

        // A folder of its own keeps the user's file name intact without ever overwriting another attachment.
        folder += Guid.NewGuid().ToString("N") + "/";
        var made = await dav.MkColAsync(folder, null, cancellationToken).ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        var href = folder + Uri.EscapeDataString(SafeName(name));
        var counted = new CountingReadStream(content, expectedSize);
        var put = await dav.PutAsync(href, counted, contentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        if (!put.IsSuccess)
        {
            await DeleteAsync(folder, cancellationToken).ConfigureAwait(false);
            return put.Error;
        }

        if (expectedSize is { } expected && (counted.Exceeded || counted.BytesRead != expected))
        {
            await DeleteAsync(folder, cancellationToken).ConfigureAwait(false);
            return counted.Exceeded
                ? new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"The upload is larger than the {expected} bytes the session was opened for.")
                : Error.Validation($"The upload holds {counted.BytesRead} bytes, but the session was opened for {expected}.");
        }

        return (href, counted.BytesRead);
    }

    /// <summary>Removes an attachment's folder; best effort, used to undo a half-finished attach.</summary>
    public async Task DeleteFolderOfAsync(string fileHref, CancellationToken cancellationToken) =>
        await DeleteAsync(fileHref[..(fileHref.LastIndexOf('/') + 1)], cancellationToken).ConfigureAwait(false);

    public Uri ToUri(string href) =>
        paths.ToRequestUri(href) ?? throw new ArgumentException("The href is outside the DAV root.", nameof(href));

    /// <summary>The Files href an <c>ATTACH</c> URL points at, or null when it is not a Nextcloud file.</summary>
    public string? ToHref(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var href = paths.ToRelative(uri.AbsolutePath);
        return href is not null && href.StartsWith(FilesRoot, StringComparison.Ordinal) && !href.EndsWith('/') ? href : null;
    }

    /// <summary>A file name Nextcloud accepts: no separators, control or reserved characters, no leading dot.</summary>
    internal static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            builder.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        var safe = builder.ToString().TrimStart('.').Trim();
        if (safe.Length > MaxSegmentLength)
        {
            var extension = Path.GetExtension(safe);
            safe = extension.Length is > 0 and < 16
                ? safe[..(MaxSegmentLength - extension.Length)] + extension
                : safe[..MaxSegmentLength];
        }

        return safe.Length == 0 ? "attachment" : safe;
    }

    private async Task DeleteAsync(string href, CancellationToken cancellationToken) =>
        _ = await dav.DeleteAsync(href, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
}

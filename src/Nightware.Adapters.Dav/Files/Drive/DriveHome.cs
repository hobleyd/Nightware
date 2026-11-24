using System.Buffers.Text;
using System.Text;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// The drive's root as the caller reaches it: the caller's Nextcloud home (ADR 0008 D1), or a shared mailbox's folder
/// mounted in it (phase 27). <see cref="Uid"/> is always the caller's: uploads and versions are theirs to make.
/// </summary>
/// <param name="Uid">The caller's uid segment exactly as the principal href carries it (still percent-encoded).</param>
internal sealed record DriveHome(string Uid)
{
    public const string DriveIdPrefix = "nc!";

    /// <summary>For a shared mailbox's drive: where its folder is mounted in the caller's home (relative, ends in <c>/</c>).</summary>
    public string? Mount { get; init; }

    /// <summary>For a shared mailbox's drive: its Nextcloud user (decoded), which names the drive.</summary>
    public string? OwnerUid { get; init; }

    public bool IsShared => Mount is not null;

    /// <summary>The drive root's href relative to the DAV root: <c>files/{uid}/</c>, or the shared folder's mount.</summary>
    public string Href => Mount ?? $"files/{Uid}/";

    /// <summary>Where chunked uploads are staged, <c>uploads/{uid}/</c>.</summary>
    public string UploadsHref => $"uploads/{Uid}/";

    public string DriveId => IdOf(OwnerUid ?? Uri.UnescapeDataString(Uid));

    /// <summary>A drive id for a (decoded) Nextcloud uid.</summary>
    public static string IdOf(string uid) => DriveIdPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(uid));

    /// <summary>The (decoded) uid a drive id names; null when it is no drive id.</summary>
    public static string? UidOf(string driveId)
    {
        if (!driveId.StartsWith(DriveIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(driveId.AsSpan(DriveIdPrefix.Length)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The path below the home (decoded, leading <c>/</c>; <c>/</c> for the home) of an href inside it; null outside.</summary>
    public string? PathOf(string relativeHref)
    {
        if (!relativeHref.StartsWith(Href, StringComparison.Ordinal) && relativeHref.TrimEnd('/') != Href.TrimEnd('/'))
        {
            return null;
        }

        var below = relativeHref.Length > Href.Length ? relativeHref[Href.Length..].TrimEnd('/') : string.Empty;
        return "/" + string.Join('/', below.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
    }
}

/// <summary>Finds the caller's <see cref="DriveHome"/> once per request scope.</summary>
internal sealed class DriveHomeLocator(IDavPrincipalDiscovery discovery, DavPaths paths, IDavClient dav)
{
    /// <summary>A shared mailbox's Nextcloud user is <c>mbx-{address}</c> (ADR 0004 D6); no one else's drive is reachable.</summary>
    public const string SharedMailboxUidPrefix = "mbx-";

    private const string Shares = "apps/files_sharing/api/v1/shares";

    private readonly Dictionary<string, Result<DriveHome>> _shared = new(StringComparer.Ordinal);
    private Task<Result<DriveHome>>? _home;

    public Task<Result<DriveHome>> CurrentAsync(CancellationToken cancellationToken) =>
        _home is { IsCompletedSuccessfully: true } cached ? cached : _home = FindAsync(cancellationToken);

    /// <summary>
    /// The caller's home, or (for a drive id naming a shared mailbox the caller is a member of) that mailbox's folder;
    /// any other drive is NotFound.
    /// </summary>
    public async Task<Result<DriveHome>> ForDriveAsync(string? driveId, CancellationToken cancellationToken)
    {
        var home = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess || driveId is null || string.Equals(driveId, home.Value.DriveId, StringComparison.Ordinal))
        {
            return home;
        }

        return DriveHome.UidOf(driveId) is { } uid && uid.StartsWith(SharedMailboxUidPrefix, StringComparison.Ordinal)
            ? await SharedAsync(home.Value, uid, cancellationToken).ConfigureAwait(false)
            : Error.NotFound("The drive does not exist, or it is not yours.");
    }

    /// <summary>
    /// The drive of the mailbox <paramref name="owner"/>: the caller's own, or a shared mailbox's folder it shared with
    /// the caller. Anyone else's drive is Forbidden: Nextcloud can't open another user's home as the caller.
    /// </summary>
    public async Task<Result<DriveHome>> ForOwnerAsync(string owner, string? driveId, CancellationToken cancellationToken)
    {
        var principal = await discovery.DiscoverAsync(new MailboxRef(owner), cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error.Kind == ErrorKind.NotFound ? NotReachable() : principal.Error;
        }

        Result<DriveHome> home;
        if (principal.Value.IsCurrentUser)
        {
            home = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var uid = paths.ToRelative(principal.Value.Href)?.TrimEnd('/').Split('/') is ["principals", "users", { Length: > 0 } segment]
                ? Uri.UnescapeDataString(segment)
                : null;
            if (uid is null || !uid.StartsWith(SharedMailboxUidPrefix, StringComparison.Ordinal))
            {
                return NotReachable();
            }

            var current = await CurrentAsync(cancellationToken).ConfigureAwait(false);
            home = current.IsSuccess ? await SharedAsync(current.Value, uid, cancellationToken).ConfigureAwait(false) : current;
        }

        return !home.IsSuccess || driveId is null || string.Equals(driveId, home.Value.DriveId, StringComparison.Ordinal)
            ? home
            : Error.NotFound("The drive does not exist, or it is not that mailbox's.");
    }

    /// <summary>
    /// The folder the shared mailbox <paramref name="uid"/> shared with the caller, found by the share (members may
    /// rename or move the mount, so never by name). None is Forbidden: the caller isn't a member.
    /// </summary>
    private async Task<Result<DriveHome>> SharedAsync(DriveHome caller, string uid, CancellationToken cancellationToken)
    {
        if (_shared.TryGetValue(uid, out var known))
        {
            return known;
        }

        var shares = await dav.OcsGetAsync(Shares, new Dictionary<string, string>(StringComparer.Ordinal) { ["shared_with_me"] = "true" }, cancellationToken)
            .ConfigureAwait(false);
        if (!shares.IsSuccess)
        {
            return shares.Error;
        }

        var target = shares.Value.ValueKind == System.Text.Json.JsonValueKind.Array
            ? shares.Value.EnumerateArray()
                .Where(s => Text(s, "uid_owner") == uid && Text(s, "item_type") == "folder" && Text(s, "file_target") is { Length: > 1 })
                .OrderBy(static s => Text(s, "share_type") == "1" ? 0 : 1)
                .Select(static s => Text(s, "file_target"))
                .FirstOrDefault()
            : null;
        Result<DriveHome> found = target is null
            ? NotReachable()
            : caller with { Mount = caller.Href + string.Join('/', target.Trim('/').Split('/').Select(Uri.EscapeDataString)) + "/", OwnerUid = uid };
        _shared[uid] = found;
        return found;
    }

    private static string? Text(System.Text.Json.JsonElement element, string name) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => value.GetString(),
                System.Text.Json.JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static Error NotReachable() =>
        Error.Forbidden("Only your own drive, or a shared mailbox's you are a member of, can be reached.");

    private async Task<Result<DriveHome>> FindAsync(CancellationToken cancellationToken)
    {
        var principal = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        return paths.ToRelative(principal.Value.Href)?.TrimEnd('/').Split('/') is ["principals", "users", { Length: > 0 } uid]
            ? new DriveHome(uid)
            : Error.NotFound("Only user accounts have a drive.");
    }
}

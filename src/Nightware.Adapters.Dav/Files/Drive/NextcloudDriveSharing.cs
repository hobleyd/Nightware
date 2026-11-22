using System.Globalization;
using System.Text.Json;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// <see cref="IDriveSharing"/> over Nextcloud's OCS share API (ADR 0008 D6): a link is a public share (type 3), an
/// invitation a user share (type 0); a permission's id is the share's id.
/// </summary>
internal sealed class NextcloudDriveSharing(IDavClient dav, DriveItemLocator locator) : IDriveSharing
{
    private const string Shares = "apps/files_sharing/api/v1/shares";
    private const string Sharees = "apps/files_sharing/api/v1/sharees";

    private const int UserShare = 0;
    private const int LinkShare = 3;

    // Nextcloud permission bits.
    private const int Read = 1;
    private const int Update = 2;
    private const int Create = 4;
    private const int Delete = 8;

    public async Task<Result<DriveShare>> CreateLinkAsync(DriveItemRef item, SharingLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await PathOfAsync(item, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = target.Value.Path,
            ["shareType"] = LinkShare.ToString(CultureInfo.InvariantCulture),
            ["permissions"] = PermissionsFor(request.Type == "edit", target.Value.IsFolder).ToString(CultureInfo.InvariantCulture),
        };
        AddExpiry(form, request.ExpirationDateTime);
        if (request.Password is { } password)
        {
            form["password"] = password;
        }

        var created = await dav.OcsSendAsync(HttpMethod.Post, Shares, form, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? ToShare(created.Value, inheritedFrom: null) : created.Error;
    }

    public async Task<Result<IReadOnlyList<DriveShare>>> InviteAsync(DriveItemRef item, DriveInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var target = await PathOfAsync(item, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var write = invitation.Roles.Contains("write");
        var shares = new List<DriveShare>();
        foreach (var recipient in invitation.Recipients)
        {
            var user = await UserForAsync(recipient.Trim(), cancellationToken).ConfigureAwait(false);
            if (!user.IsSuccess)
            {
                return user.Error;
            }

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = target.Value.Path,
                ["shareType"] = UserShare.ToString(CultureInfo.InvariantCulture),
                ["shareWith"] = user.Value,
                ["permissions"] = PermissionsFor(write, target.Value.IsFolder).ToString(CultureInfo.InvariantCulture),
            };
            AddExpiry(form, invitation.ExpirationDateTime);
            var created = await dav.OcsSendAsync(HttpMethod.Post, Shares, form, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created.Error;
            }

            shares.Add(ToShare(created.Value, inheritedFrom: null) with { GrantedTo = new EmailAddress(recipient.Trim(), Text(created.Value, "share_with_displayname")) });
        }

        return shares;
    }

    public async Task<Result<IReadOnlyList<DriveShare>>> ListPermissionsAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var target = await PathOfAsync(item, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var own = await dav.OcsGetAsync(Shares, new Dictionary<string, string> { ["path"] = target.Value.Path, ["reshares"] = "true" }, cancellationToken).ConfigureAwait(false);
        if (!own.IsSuccess)
        {
            return own.Error;
        }

        var shares = Array(own.Value).Select(s => ToShare(s, inheritedFrom: null)).ToList();
        if (target.Value.Path != "/")
        {
            // Shares of the folders above; Nextcloud answers 404 for an item no ancestor of which is shared.
            var inherited = await dav.OcsGetAsync($"{Shares}/inherited", new Dictionary<string, string> { ["path"] = target.Value.Path }, cancellationToken).ConfigureAwait(false);
            if (inherited.IsSuccess)
            {
                shares.AddRange(Array(inherited.Value).Select(s => ToShare(s, inheritedFrom: Text(s, "item_source") ?? Text(s, "file_source") ?? string.Empty)));
            }
            else if (inherited.Error.Kind != ErrorKind.NotFound)
            {
                return inherited.Error;
            }
        }

        return shares;
    }

    public async Task<Result> DeletePermissionAsync(DriveItemRef item, string permissionId, CancellationToken cancellationToken)
    {
        var listed = await ListPermissionsAsync(item, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var share = listed.Value.FirstOrDefault(s => s.Id == permissionId);
        if (share is null)
        {
            return Error.NotFound("The item has no such permission.");
        }

        if (share.InheritedFromId is not null)
        {
            return Error.Forbidden("The permission is inherited from a folder above; remove it there.");
        }

        var deleted = await dav.OcsSendAsync(HttpMethod.Delete, $"{Shares}/{Uri.EscapeDataString(permissionId)}", null, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Result.Success() : deleted.Error;
    }

    private async Task<Result<(string Path, bool IsFolder)>> PathOfAsync(DriveItemRef item, CancellationToken cancellationToken)
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

        return (home.Value.PathOf(located.Value.Href) ?? "/", located.Value.IsFolder);
    }

    /// <summary>The Nextcloud user an address belongs to, from the sharee search (exact matches only).</summary>
    private async Task<Result<string>> UserForAsync(string address, CancellationToken cancellationToken)
    {
        var found = await dav.OcsGetAsync(Sharees, new Dictionary<string, string>
        {
            ["search"] = address,
            ["itemType"] = "file",
            ["shareType[]"] = UserShare.ToString(CultureInfo.InvariantCulture),
            ["perPage"] = "5",
        }, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var exact = found.Value.ValueKind == JsonValueKind.Object && found.Value.TryGetProperty("exact", out var e) && e.TryGetProperty("users", out var users)
            ? Array(users).ToList()
            : [];
        var matches = exact.Where(u => string.Equals(Text(u, "shareWithDisplayNameUnique"), address, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0 && exact.Count == 1)
        {
            matches = exact;
        }

        if (matches.Count == 1 && matches[0].TryGetProperty("value", out var value) && Text(value, "shareWith") is { Length: > 0 } uid)
        {
            return uid;
        }

        return Error.Validation($"'{address}' is not a user of this server.", "recipients");
    }

    internal static DriveShare ToShare(JsonElement share, string? inheritedFrom)
    {
        var permissions = (int)(Long(share, "permissions") ?? Read);
        var write = (permissions & (Update | Create | Delete)) != 0;
        var isLink = Long(share, "share_type") == LinkShare;
        var expiration = DateTimeOffset.TryParse(Text(share, "expiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
            ? expires.ToUniversalTime()
            : (DateTimeOffset?)null;
        return new DriveShare(Text(share, "id") ?? string.Empty, [write ? "write" : "read"])
        {
            Link = isLink && Text(share, "url") is { } url ? new SharingLink(write ? "edit" : "view", "anonymous", url) : null,
            GrantedTo = !isLink && Text(share, "share_with") is { } with
                ? new EmailAddress(Text(share, "share_with_email") ?? with, Text(share, "share_with_displayname"))
                : null,
            ExpirationDateTime = expiration,
            HasPassword = isLink && !string.IsNullOrEmpty(Text(share, "password") ?? Text(share, "share_with")),
            InheritedFromId = inheritedFrom,
        };
    }

    private static int PermissionsFor(bool write, bool isFolder) =>
        !write ? Read : isFolder ? Read | Update | Create | Delete : Read | Update;

    private static void AddExpiry(Dictionary<string, string> form, DateTimeOffset? expiration)
    {
        if (expiration is { } date)
        {
            form["expireDate"] = date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private static List<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray()] : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Long(JsonElement element, string name) =>
        long.TryParse(Text(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}

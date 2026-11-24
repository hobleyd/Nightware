using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// <see cref="ISharedCalendars"/> on Nextcloud (ADR 0004 D6). The mailbox's user is a local Nextcloud user named
/// <c>mbx-{address}</c> with the mailbox's address (so <c>principal-property-search</c> finds it) and its name. Only
/// a calendar's owner can share it, so each repair sets the user a fresh random password through OCS, acts as the
/// user once over WebDAV (Basic) and keeps nothing: nobody knows a password with which to sign in. Nextcloud creates
/// the <c>personal</c> calendar on the user's first WebDAV request, with a sample event that a new account's
/// calendar loses at once.
/// </summary>
internal sealed partial class NextcloudSharedCalendars(NextcloudProvisioningClient client) : ISharedCalendars
{
    private const string UsersPath = "ocs/v1.php/cloud/users";
    private const string Calendar = "personal";
    private const string AddressBook = "contacts";
    private const string Propfind = """<?xml version="1.0"?><d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/"><d:prop><d:resourcetype/><cs:invite/></d:prop></d:propfind>""";

    internal static string Uid(MailAddress address) => "mbx-" + address.Value.ToLowerInvariant();

    public async Task<Result<SharedCalendarAccount>> FindAsync(MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var uid = Uid(address);
        var user = await client.SendAsync(HttpMethod.Get, $"{UsersPath}/{Uri.EscapeDataString(uid)}", null, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        if (!user.Value.Succeeded)
        {
            return user.Value.ToError($"No Nextcloud user {uid}.");
        }

        // Who the calendar is shared with only its owner can see, and inspecting must not reset the password, so a
        // missing share is something Repair (EnsureAsync, idempotent) puts right rather than something reported.
        var name = (user.Value.Data as JsonObject)?["displayname"]?.GetValue<string>();
        return new SharedCalendarAccount(uid, name);
    }

    public async Task<Result> EnsureAsync(MailAddress address, string displayName, string groupId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var uid = Uid(address);
        var password = NewPassword();
        var existing = await client.SendAsync(HttpMethod.Get, $"{UsersPath}/{Uri.EscapeDataString(uid)}", null, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        var created = false;
        if (existing.Value.Succeeded)
        {
            foreach (var (key, value) in new[] { ("password", password), ("email", address.Value), ("displayname", displayName) })
            {
                var edited = await client.SendAsync(HttpMethod.Put, $"{UsersPath}/{Uri.EscapeDataString(uid)}", new JsonObject { ["key"] = key, ["value"] = value }, cancellationToken)
                    .ConfigureAwait(false);
                if (!edited.IsSuccess || !edited.Value.Succeeded)
                {
                    return edited.IsSuccess ? edited.Value.ToError($"No Nextcloud user {uid}.") : edited.Error;
                }
            }
        }
        else
        {
            // The email is set after creating: created with one, Nextcloud mails a welcome to it, which is the
            // shared mailbox's own inbox.
            var body = new JsonObject { ["userid"] = uid, ["password"] = password, ["displayName"] = displayName };
            var added = await client.SendAsync(HttpMethod.Post, UsersPath, body, cancellationToken).ConfigureAwait(false);
            if (!added.IsSuccess || !added.Value.Succeeded)
            {
                return added.IsSuccess ? added.Value.ToError($"Nextcloud refused the user {uid}.") : added.Error;
            }

            var mailed = await client.SendAsync(HttpMethod.Put, $"{UsersPath}/{Uri.EscapeDataString(uid)}", new JsonObject { ["key"] = "email", ["value"] = address.Value }, cancellationToken)
                .ConfigureAwait(false);
            if (!mailed.IsSuccess || !mailed.Value.Succeeded)
            {
                return mailed.IsSuccess ? mailed.Value.ToError($"No Nextcloud user {uid}.") : mailed.Error;
            }

            created = true;
        }

        // The user's first WebDAV request makes its calendars.
        var home = $"remote.php/dav/calendars/{Uri.EscapeDataString(uid)}/";
        var listed = await client.SendDavAsync(new HttpMethod("PROPFIND"), home + Calendar + "/", uid, password, Propfind, "1", cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        if (listed.Value.Status != 207)
        {
            return Error.BackendUnavailable($"Nextcloud answered {listed.Value.Status} for {uid}'s calendar.");
        }

        if (created)
        {
            foreach (Match resource in ResourceHref().Matches(listed.Value.Body))
            {
                await client.SendDavAsync(HttpMethod.Delete, home + Calendar + "/" + resource.Groups[1].Value, uid, password, null, null, cancellationToken).ConfigureAwait(false);
            }
        }

        // Sharing again with the same access changes nothing (Sabre's sharing plugin), so this is idempotent. The group
        // id is URL-encoded in the principal: Nextcloud accepts "principals/groups/a@b" too, but grants nothing by it.
        var share = $"""
            <?xml version="1.0"?><o:share xmlns:d="DAV:" xmlns:o="http://owncloud.org/ns"><o:set><d:href>principal:principals/groups/{System.Security.SecurityElement.Escape(Uri.EscapeDataString(groupId))}</d:href><o:read-write/></o:set></o:share>
            """;
        var shared = await client.SendDavAsync(HttpMethod.Post, home + Calendar + "/", uid, password, share, null, cancellationToken).ConfigureAwait(false);
        if (!shared.IsSuccess)
        {
            return shared.Error;
        }

        if (shared.Value.Status is < 200 or >= 300)
        {
            return Error.BackendUnavailable($"Nextcloud answered {shared.Value.Status} when sharing {uid}'s calendar.");
        }

        // Phase 27: the address book too. The user's first CardDAV request makes its "contacts" book; the same share
        // gives the members it as "Contacts ({name})", which /users/{address}/contacts reads.
        var book = $"remote.php/dav/addressbooks/users/{Uri.EscapeDataString(uid)}/{AddressBook}/";
        var touched = await client.SendDavAsync(new HttpMethod("PROPFIND"), book, uid, password, Propfind, "0", cancellationToken).ConfigureAwait(false);
        if (!touched.IsSuccess)
        {
            return touched.Error;
        }

        if (touched.Value.Status != 207)
        {
            return Error.BackendUnavailable($"Nextcloud answered {touched.Value.Status} for {uid}'s address book.");
        }

        var sharedBook = await client.SendDavAsync(HttpMethod.Post, book, uid, password, share, null, cancellationToken).ConfigureAwait(false);
        if (!sharedBook.IsSuccess)
        {
            return sharedBook.Error;
        }

        if (sharedBook.Value.Status is < 200 or >= 300)
        {
            return Error.BackendUnavailable($"Nextcloud answered {sharedBook.Value.Status} when sharing {uid}'s address book.");
        }

        return await EnsureFilesAsync(uid, password, displayName, groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 27: the mailbox's files, a folder in its user's home shared read-write with the group, which members
    /// reach as <c>/users/{address}/drive</c>. An existing share to the group is kept as it is (the folder may have been
    /// renamed); otherwise the folder, named after the mailbox, is made (or reused) and shared.
    /// </summary>
    private async Task<Result> EnsureFilesAsync(string uid, string password, string displayName, string groupId, CancellationToken cancellationToken)
    {
        const string Shares = "ocs/v2.php/apps/files_sharing/api/v1/shares";
        var listed = await client.SendDavAsync(HttpMethod.Get, Shares + "?format=json", uid, password, null, null, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        if (listed.Value.Status != 200)
        {
            return Error.BackendUnavailable($"Nextcloud answered {listed.Value.Status} when listing {uid}'s shares.");
        }

        if (SharesFolderWith(listed.Value.Body, groupId))
        {
            return Result.Success();
        }

        var folder = FolderName(displayName);
        var made = await client.SendDavAsync(new HttpMethod("MKCOL"), $"remote.php/dav/files/{Uri.EscapeDataString(uid)}/{Uri.EscapeDataString(folder)}/", uid, password, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (!made.IsSuccess)
        {
            return made.Error;
        }

        // 405: the folder is already there (an earlier repair made it, and its share went).
        if (made.Value.Status is not (201 or 405))
        {
            return Error.BackendUnavailable($"Nextcloud answered {made.Value.Status} when making {uid}'s files folder.");
        }

        var query = $"?path={Uri.EscapeDataString("/" + folder)}&shareType=1&shareWith={Uri.EscapeDataString(groupId)}&permissions=31&format=json";
        var shared = await client.SendDavAsync(HttpMethod.Post, Shares + query, uid, password, null, null, cancellationToken).ConfigureAwait(false);
        if (!shared.IsSuccess)
        {
            return shared.Error;
        }

        return shared.Value.Status == 200
            ? Result.Success()
            : Error.BackendUnavailable($"Nextcloud answered {shared.Value.Status} when sharing {uid}'s files.");
    }

    /// <summary>True when the user's shares (OCS JSON) include a folder shared with the group.</summary>
    internal static bool SharesFolderWith(string body, string groupId)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("ocs", out var ocs) && ocs.TryGetProperty("data", out var data)
                && data.ValueKind == System.Text.Json.JsonValueKind.Array
                && data.EnumerateArray().Any(s =>
                    s.TryGetProperty("share_type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.Number && type.GetInt32() == 1
                    && s.TryGetProperty("share_with", out var with) && string.Equals(with.GetString(), groupId, StringComparison.Ordinal)
                    && s.TryGetProperty("item_type", out var item) && item.GetString() == "folder");
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The mailbox's name as a folder name: no separators or control characters, never empty.</summary>
    internal static string FolderName(string displayName)
    {
        var cleaned = new string([.. displayName.Where(static c => c is not ('/' or '\\') && !char.IsControl(c))]).Trim().Trim('.');
        return cleaned.Length == 0 ? "Files" : cleaned;
    }

    public async Task<Result> DeleteAsync(MailAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var uid = Uid(address);
        var deleted = await client.SendAsync(HttpMethod.Delete, $"{UsersPath}/{Uri.EscapeDataString(uid)}", null, cancellationToken).ConfigureAwait(false);
        if (!deleted.IsSuccess)
        {
            return deleted.Error;
        }

        return deleted.Value.Succeeded ? Result.Success() : deleted.Value.ToError($"No Nextcloud user {uid}.");
    }

    /// <summary>Random, and mixed enough for Nextcloud's password policy.</summary>
    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', 'x').Replace('/', 'y') + "Aa1!";

    [GeneratedRegex(@"/personal/([^/<]+\.ics)</d:href>", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceHref();
}

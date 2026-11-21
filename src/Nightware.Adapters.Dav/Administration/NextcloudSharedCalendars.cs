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
            var body = new JsonObject { ["userid"] = uid, ["password"] = password, ["email"] = address.Value, ["displayName"] = displayName };
            var added = await client.SendAsync(HttpMethod.Post, UsersPath, body, cancellationToken).ConfigureAwait(false);
            if (!added.IsSuccess || !added.Value.Succeeded)
            {
                return added.IsSuccess ? added.Value.ToError($"Nextcloud refused the user {uid}.") : added.Error;
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

        return shared.Value.Status is >= 200 and < 300
            ? Result.Success()
            : Error.BackendUnavailable($"Nextcloud answered {shared.Value.Status} when sharing {uid}'s calendar.");
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

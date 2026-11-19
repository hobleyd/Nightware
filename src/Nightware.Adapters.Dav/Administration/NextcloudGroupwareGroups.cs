using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// Nextcloud groups through the provisioning API (<c>/ocs/v1.php/cloud/groups</c>) and team folders through the
/// <c>groupfolders</c> app's OCS routes (<c>/index.php/apps/groupfolders/folders</c>), with the console's own
/// account (phase 9 step 0 d, e). Membership changes take effect at once, for tokens issued before them too.
/// </summary>
/// <remarks>
/// OCS codes, from <c>provisioning_api</c>'s <c>GroupsController</c> and <c>UsersController</c>: <c>POST groups</c>
/// answers 102 when the group exists; <c>DELETE groups/{gid}</c> 101 when it does not; <c>POST|DELETE
/// users/{uid}/groups</c> 102 for a missing group and 103 for a missing user. All in an HTTP 200 on v1.
/// </remarks>
internal sealed class NextcloudGroupwareGroups(NextcloudProvisioningClient client) : IGroupwareGroups
{
    private const string GroupsPath = "ocs/v1.php/cloud/groups";
    private const string UsersPath = "ocs/v1.php/cloud/users/";
    private const string FoldersPath = "index.php/apps/groupfolders/folders";

    /// <summary>Read, update, create and delete; not share (groupfolders' permission bits 1, 2, 4, 8).</summary>
    internal const int FolderPermissions = 15;

    public async Task<Result<GroupwareGroup>> FindAsync(string groupId, CancellationToken cancellationToken)
    {
        if (InvalidId(groupId) is { } invalid)
        {
            return invalid;
        }

        // The details search is a substring match: the group is the one whose id is exactly groupId.
        Result<OcsReply> details = await client.SendAsync(HttpMethod.Get, $"{GroupsPath}/details?search={Escape(groupId)}", null, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess || !details.Value.Succeeded)
        {
            return details.IsSuccess ? details.Value.ToError($"No Nextcloud group {groupId}.") : details.Error;
        }

        JsonObject? group = (details.Value.Data as JsonObject)?["groups"] is JsonArray groups
            ? groups.OfType<JsonObject>().FirstOrDefault(g => string.Equals(Text(g, "id"), groupId, StringComparison.Ordinal))
            : null;
        if (group is null)
        {
            return Error.NotFound($"No Nextcloud group {groupId}.");
        }

        Result<OcsReply> users = await client.SendAsync(HttpMethod.Get, $"{GroupsPath}/{Escape(groupId)}/users", null, cancellationToken).ConfigureAwait(false);
        if (!users.IsSuccess || !users.Value.Succeeded)
        {
            return users.IsSuccess ? users.Value.ToError($"No Nextcloud group {groupId}.") : users.Error;
        }

        IReadOnlyList<string> members = (users.Value.Data as JsonObject)?["users"] is JsonArray list
            ? [.. list.OfType<JsonValue>().Where(static v => v.GetValueKind() == JsonValueKind.String).Select(static v => v.GetValue<string>())]
            : [];
        return new GroupwareGroup(groupId, Text(group, "displayname"), members);
    }

    public async Task<Result> CreateAsync(string groupId, string displayName, CancellationToken cancellationToken)
    {
        if (InvalidId(groupId) is { } invalid)
        {
            return invalid;
        }

        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Post, GroupsPath, new JsonObject { ["groupid"] = groupId, ["displayname"] = displayName }, cancellationToken).ConfigureAwait(false);
        return Outcome(sent, groupId, reply => reply.StatusCode == 102 ? Error.Conflict($"Nextcloud already has a group {groupId}.", "groupId") : null);
    }

    public async Task<Result> SetDisplayNameAsync(string groupId, string displayName, CancellationToken cancellationToken)
    {
        if (InvalidId(groupId) is { } invalid)
        {
            return invalid;
        }

        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Put, $"{GroupsPath}/{Escape(groupId)}", new JsonObject { ["key"] = "displayname", ["value"] = displayName }, cancellationToken).ConfigureAwait(false);
        return Outcome(sent, groupId, null);
    }

    public async Task<Result> AddMemberAsync(string groupId, string uid, CancellationToken cancellationToken) =>
        await MembershipAsync(HttpMethod.Post, groupId, uid, cancellationToken).ConfigureAwait(false);

    public async Task<Result> RemoveMemberAsync(string groupId, string uid, CancellationToken cancellationToken) =>
        await MembershipAsync(HttpMethod.Delete, groupId, uid, cancellationToken).ConfigureAwait(false);

    public async Task<Result> DeleteAsync(string groupId, CancellationToken cancellationToken)
    {
        if (InvalidId(groupId) is { } invalid)
        {
            return invalid;
        }

        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Delete, $"{GroupsPath}/{Escape(groupId)}", null, cancellationToken).ConfigureAwait(false);
        return Outcome(sent, groupId, reply => reply.StatusCode == 101 ? Error.NotFound($"No Nextcloud group {groupId}.") : null);
    }

    public async Task<Result<TeamFolder>> FindFolderAsync(int folderId, CancellationToken cancellationToken)
    {
        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Get, $"{FoldersPath}/{folderId}", null, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        // A missing folder is an HTTP 404, or an OCS success with no data, depending on the app's version.
        if (sent.Value.HttpStatus == 404 || (sent.Value.Succeeded && sent.Value.Data is not JsonObject))
        {
            return Error.NotFound($"No team folder {folderId}.");
        }

        return sent.Value.Succeeded ? ToFolder(sent.Value.Data) : sent.Value.ToError($"No team folder {folderId}.");
    }

    public async Task<Result<TeamFolder>> CreateFolderAsync(string mountPoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);
        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Post, FoldersPath, new JsonObject { ["mountpoint"] = mountPoint.Trim() }, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        if (sent.Value.IsOcsNotFound || sent.Value.HttpStatus == 404)
        {
            return Error.Unsupported("Nextcloud's Team folders app (groupfolders) is not enabled.");
        }

        return sent.Value.Succeeded ? ToFolder(sent.Value.Data) : sent.Value.ToError("Nextcloud did not create the team folder.");
    }

    public async Task<Result> GrantFolderAsync(int folderId, string groupId, CancellationToken cancellationToken)
    {
        if (InvalidId(groupId) is { } invalid)
        {
            return invalid;
        }

        Result<OcsReply> shared = await client.SendAsync(HttpMethod.Post, $"{FoldersPath}/{folderId}/groups", new JsonObject { ["group"] = groupId }, cancellationToken).ConfigureAwait(false);
        Result result = Outcome(shared, groupId, null);
        if (!result.IsSuccess)
        {
            return result;
        }

        Result<OcsReply> permitted = await client.SendAsync(HttpMethod.Post, $"{FoldersPath}/{folderId}/groups/{Escape(groupId)}", new JsonObject { ["permissions"] = FolderPermissions }, cancellationToken).ConfigureAwait(false);
        return Outcome(permitted, groupId, null);
    }

    public async Task<Result> DeleteFolderAsync(int folderId, CancellationToken cancellationToken)
    {
        Result<OcsReply> sent = await client.SendAsync(HttpMethod.Delete, $"{FoldersPath}/{folderId}", null, cancellationToken).ConfigureAwait(false);
        if (sent.IsSuccess && sent.Value.HttpStatus == 404)
        {
            return Error.NotFound($"No team folder {folderId}.");
        }

        return Outcome(sent, folderId.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
    }

    private async Task<Result> MembershipAsync(HttpMethod method, string groupId, string uid, CancellationToken cancellationToken)
    {
        if ((InvalidId(groupId) ?? InvalidId(uid)) is { } invalid)
        {
            return invalid;
        }

        Result<OcsReply> sent = await client.SendAsync(method, $"{UsersPath}{Escape(uid)}/groups?groupid={Escape(groupId)}", null, cancellationToken).ConfigureAwait(false);
        return Outcome(sent, groupId, reply => reply.StatusCode switch
        {
            102 => Error.NotFound($"No Nextcloud group {groupId}."),
            103 => Error.NotFound($"No Nextcloud user {uid}.", "uid"),
            _ => null,
        });
    }

    /// <summary>Success, the caller's reading of a known OCS code, or the reply's generic error.</summary>
    private static Result Outcome(Result<OcsReply> sent, string subject, Func<OcsReply, Error?>? known)
    {
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        OcsReply reply = sent.Value;
        if (reply.Succeeded)
        {
            return Result.Success();
        }

        return (reply.IsEnvelope ? known?.Invoke(reply) : null) ?? reply.ToError($"Nextcloud has no {subject}.");
    }

    private static Result<TeamFolder> ToFolder(JsonNode? data)
    {
        if (data is not JsonObject folder || folder["id"] is not JsonValue id || !id.TryGetValue(out int folderId))
        {
            return Error.BackendUnavailable("Nextcloud sent an unreadable team folder.");
        }

        // "groups" is an object of group id to permissions, or an empty array when shared with no one.
        IReadOnlySet<string> groups = folder["groups"] is JsonObject shared ? shared.Select(static g => g.Key).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        return new TeamFolder(folderId, Text(folder, "mount_point") ?? string.Empty, groups);
    }

    private static Error? InvalidId(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains('/', StringComparison.Ordinal) ? Error.Validation("Not a Nextcloud id.", "id") : null;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string? Text(JsonObject data, string name) =>
        data[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text : null;
}

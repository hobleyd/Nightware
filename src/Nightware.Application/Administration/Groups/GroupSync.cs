using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Groups;

/// <summary>A group with its entity, its members and its state in each backend.</summary>
public sealed record GroupDetail(Entity Entity, Group Group, IReadOnlyList<IdentityAccount> Members, IReadOnlyList<BackendLink> Links)
{
    /// <summary>Null when the group has no facet for that backend.</summary>
    public BackendLink? For(BackendKind backend) => Links.SingleOrDefault(l => l.Backend == backend);
}

/// <param name="Notes">What could not be done yet and needs nothing from the admin (a member who has not signed in to Nextcloud).</param>
public sealed record GroupSyncOutcome(Group Group, IReadOnlyList<string> Notes);

/// <summary>
/// Derives what each backend should hold for a group from the record (Keycloak) and brings the backends into line
/// (phase 9). Membership always follows the record; only enabled members are pushed, so an offboarded member
/// stops receiving the list's mail and loses the group's files, and comes back when reinstated. Removing a facet
/// is the caller's job: this only builds what the group's facets ask for.
/// </summary>
public sealed class GroupSync(IEntityRegistry entities, IEntityGroups groups, IMailingLists lists, IGroupwareGroups groupware, IGroupwareAccounts groupwareAccounts)
{
    /// <summary>Where a list is delivered: the enabled members' addresses, then the outside recipients.</summary>
    public static IReadOnlyList<string> Recipients(GroupMail mail, IEnumerable<IdentityAccount> members)
    {
        ArgumentNullException.ThrowIfNull(mail);
        return Normalise(Enabled(members).Select(static m => m.Email).Concat(mail.ExternalRecipients.Select(static r => r.Value)));
    }

    /// <summary>The addresses enabled members may send from: their own and their aliases.</summary>
    public static IReadOnlyList<string> MemberSenders(IEnumerable<IdentityAccount> members) =>
        Normalise(Enabled(members).SelectMany(static m => m.Aliases.Prepend(m.Email)));

    /// <summary>The groupware uids that belong in the group: the enabled members' usernames.</summary>
    public static IReadOnlyList<string> GroupwareMembers(IEnumerable<IdentityAccount> members) =>
        [.. Enabled(members).Select(static m => m.Username).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public async Task<IReadOnlyList<BackendLink>> InspectAsync(Entity entity, Group group, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        var links = new List<BackendLink> { new(BackendKind.Identity, LinkState.Linked, $"{members.Count} member(s).", group.Id) };
        if (group.Mail is { } mail)
        {
            links.Add(await InspectMailAsync(mail, members, cancellationToken).ConfigureAwait(false));
        }

        if (group.Files is { } files)
        {
            links.Add(await InspectGroupwareAsync(entity, group, files, members, cancellationToken).ConfigureAwait(false));
        }

        return links;
    }

    /// <summary>Every step runs even when an earlier one fails; the first failure is returned, with every failure in its message.</summary>
    public async Task<Result<GroupSyncOutcome>> ApplyAsync(Entity entity, Group group, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);
        var failures = new List<Error>();
        var notes = new List<string>();
        if (group.Mail is { } mail)
        {
            Result applied = await ApplyMailAsync(entity, group, mail, members, cancellationToken).ConfigureAwait(false);
            if (!applied.IsSuccess)
            {
                failures.Add(applied.Error);
            }
        }

        if (group.Files is { } files)
        {
            Result<Group> applied = await ApplyGroupwareAsync(entity, group, files, members, notes, cancellationToken).ConfigureAwait(false);
            if (applied.IsSuccess)
            {
                group = applied.Value;
            }
            else
            {
                failures.Add(applied.Error);
            }
        }

        if (failures.Count == 0)
        {
            return new GroupSyncOutcome(group, notes);
        }

        Error first = failures[0];
        return failures.Count == 1 ? first : first with { Message = string.Join(" ", failures.Select(static f => f.Message)) };
    }

    /// <summary>Brings every group the user belongs to back into line (offboarding, reinstating), in every entity they are a member of.</summary>
    public async Task<Result> ApplyForUserAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return Error.BackendUnavailable($"Groups: {memberships.Error.Message}");
        }

        var failures = new List<string>();
        foreach (string entityId in memberships.Value)
        {
            Result<Entity> loaded = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                failures.Add(loaded.Error.Message);
                continue;
            }

            Entity entity = loaded.Value;
            Result<IReadOnlyList<Group>> mine = await groups.ListUserGroupsAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                failures.Add(mine.Error.Message);
                continue;
            }

            foreach (Group group in mine.Value)
            {
                Result<IReadOnlyList<IdentityAccount>> members = await groups.ListMembersAsync(entity.Id, group.Id, cancellationToken).ConfigureAwait(false);
                Result<GroupSyncOutcome> applied = members.IsSuccess ? await ApplyAsync(entity, group, members.Value, cancellationToken).ConfigureAwait(false) : members.Error;
                if (!applied.IsSuccess)
                {
                    failures.Add($"{group.Name}: {applied.Error.Message}");
                }
            }
        }

        return failures.Count == 0 ? Result.Success() : Error.BackendUnavailable($"Groups: {string.Join(" ", failures)}");
    }

    private async Task<BackendLink> InspectMailAsync(GroupMail mail, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        Result<MailingList> found = await lists.FindByAddressAsync(mail.Address, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Mail, LinkState.Missing, $"No list at {mail.Address}.")
                : new BackendLink(BackendKind.Mail, LinkState.Error, found.Error.Message);
        }

        string? drift = Difference("recipients", Recipients(mail, members), found.Value.Recipients)
            ?? Difference("aliases", Normalise(mail.Aliases.Select(static a => a.Value)), found.Value.Aliases);
        return drift is null
            ? new BackendLink(BackendKind.Mail, LinkState.Linked, $"{mail.Address}", found.Value.Id)
            : new BackendLink(BackendKind.Mail, LinkState.Conflict, $"The list is out of step with the group ({drift}); Repair brings it into line.", found.Value.Id);
    }

    private async Task<BackendLink> InspectGroupwareAsync(Entity entity, Group group, GroupFiles files, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        string groupId = group.Key.GroupwareId(entity.Alias);
        Result<GroupwareGroup> found = await groupware.FindAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Groupware, LinkState.Missing, $"No Nextcloud group {groupId}.")
                : new BackendLink(BackendKind.Groupware, LinkState.Error, found.Error.Message);
        }

        // A member who has never signed in to Nextcloud cannot be in a group yet: waiting, not out of step.
        var expected = GroupwareMembers(members).ToList();
        foreach (string uid in expected.Except(found.Value.MemberUids, StringComparer.Ordinal).ToList())
        {
            Result<GroupwareAccount> account = await groupwareAccounts.FindAsync(uid, cancellationToken).ConfigureAwait(false);
            if (!account.IsSuccess && account.Error.Kind == ErrorKind.NotFound)
            {
                expected.Remove(uid);
            }
        }

        string? drift = Difference("members", expected, found.Value.MemberUids);
        if (drift is not null)
        {
            return new BackendLink(BackendKind.Groupware, LinkState.Conflict, $"The Nextcloud group is out of step with the group ({drift}); Repair brings it into line.", groupId);
        }

        if (files.TeamFolder)
        {
            Result<TeamFolder> folder = files.TeamFolderId is { } folderId
                ? await groupware.FindFolderAsync(folderId, cancellationToken).ConfigureAwait(false)
                : Error.NotFound("No team folder yet.");
            if (!folder.IsSuccess)
            {
                return folder.Error.Kind == ErrorKind.NotFound
                    ? new BackendLink(BackendKind.Groupware, LinkState.Missing, "The team folder does not exist yet; Repair creates it.", groupId)
                    : new BackendLink(BackendKind.Groupware, LinkState.Error, folder.Error.Message, groupId);
            }

            if (!folder.Value.Groups.Contains(groupId))
            {
                return new BackendLink(BackendKind.Groupware, LinkState.Conflict, $"The team folder \"{folder.Value.MountPoint}\" is not shared with the group; Repair shares it.", groupId);
            }
        }

        return new BackendLink(BackendKind.Groupware, LinkState.Linked, files.TeamFolder ? "Group and team folder." : "Group.", groupId);
    }

    private async Task<Result> ApplyMailAsync(Entity entity, Group group, GroupMail mail, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> recipients = Recipients(mail, members);
        Result<MailingList> found = await lists.FindByAddressAsync(mail.Address, cancellationToken).ConfigureAwait(false);
        Result written;
        if (found.IsSuccess)
        {
            bool same = Difference("recipients", recipients, found.Value.Recipients) is null
                && Difference("aliases", Normalise(mail.Aliases.Select(static a => a.Value)), found.Value.Aliases) is null;
            written = same ? Result.Success() : await lists.UpdateAsync(found.Value.Id, mail.Aliases, recipients, cancellationToken).ConfigureAwait(false);
        }
        else if (found.Error.Kind == ErrorKind.NotFound)
        {
            Result<MailingList> created = await lists.CreateAsync(mail.Address, mail.Aliases, recipients, $"{entity.Name}: {group.Name}", cancellationToken).ConfigureAwait(false);
            written = created.IsSuccess ? Result.Success() : created.Error;
        }
        else
        {
            written = found.Error;
        }

        // The sender rule goes on even when the list could not be written: a list that exists must never be left
        // open to senders its policy refuses.
        Result ruled = await lists.SetSenderRuleAsync(mail.Address, SenderRule.For(mail, MemberSenders(members), entity.Domains), cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? ruled : written;
    }

    private async Task<Result<Group>> ApplyGroupwareAsync(Entity entity, Group group, GroupFiles files, IReadOnlyList<IdentityAccount> members, List<string> notes, CancellationToken cancellationToken)
    {
        string groupId = group.Key.GroupwareId(entity.Alias);
        Result<GroupwareGroup> found = await groupware.FindAsync(groupId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> current;
        if (found.IsSuccess)
        {
            current = found.Value.MemberUids;
            if (!string.Equals(found.Value.DisplayName, group.Name, StringComparison.Ordinal))
            {
                Result renamed = await groupware.SetDisplayNameAsync(groupId, group.Name, cancellationToken).ConfigureAwait(false);
                if (!renamed.IsSuccess)
                {
                    return renamed.Error;
                }
            }
        }
        else if (found.Error.Kind == ErrorKind.NotFound)
        {
            Result created = await groupware.CreateAsync(groupId, group.Name, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created.Error;
            }

            current = [];
        }
        else
        {
            return found.Error;
        }

        IReadOnlyList<string> expected = GroupwareMembers(members);
        foreach (string uid in expected.Except(current, StringComparer.Ordinal))
        {
            Result added = await groupware.AddMemberAsync(groupId, uid, cancellationToken).ConfigureAwait(false);
            if (!added.IsSuccess && added.Error.Kind == ErrorKind.NotFound)
            {
                notes.Add($"{uid} has not signed in to Nextcloud yet; Repair adds them once they have.");
            }
            else if (!added.IsSuccess)
            {
                return added.Error;
            }
        }

        foreach (string uid in current.Except(expected, StringComparer.Ordinal))
        {
            Result removed = await groupware.RemoveMemberAsync(groupId, uid, cancellationToken).ConfigureAwait(false);
            if (!removed.IsSuccess)
            {
                return removed.Error;
            }
        }

        return files.TeamFolder ? await ApplyTeamFolderAsync(group, files, groupId, cancellationToken).ConfigureAwait(false) : group;
    }

    private async Task<Result<Group>> ApplyTeamFolderAsync(Group group, GroupFiles files, string groupId, CancellationToken cancellationToken)
    {
        Result<TeamFolder> folder = files.TeamFolderId is { } folderId
            ? await groupware.FindFolderAsync(folderId, cancellationToken).ConfigureAwait(false)
            : Error.NotFound("No team folder yet.");
        if (!folder.IsSuccess && folder.Error.Kind == ErrorKind.NotFound)
        {
            folder = await groupware.CreateFolderAsync(group.Name, cancellationToken).ConfigureAwait(false);
            if (!folder.IsSuccess)
            {
                return folder.Error;
            }

            // Recorded before sharing, so a failed share is retried on this folder, never on a second one.
            Result<Group> recorded = await groups.UpdateAsync(group with { Files = files with { TeamFolderId = folder.Value.Id } }, cancellationToken).ConfigureAwait(false);
            if (!recorded.IsSuccess)
            {
                return recorded.Error;
            }

            group = recorded.Value;
        }
        else if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        if (!folder.Value.Groups.Contains(groupId))
        {
            Result granted = await groupware.GrantFolderAsync(folder.Value.Id, groupId, cancellationToken).ConfigureAwait(false);
            if (!granted.IsSuccess)
            {
                return granted.Error;
            }
        }

        return group;
    }

    private static IEnumerable<IdentityAccount> Enabled(IEnumerable<IdentityAccount> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return members.Where(static m => m.Enabled);
    }

    private static IReadOnlyList<string> Normalise(IEnumerable<string> addresses) =>
        [.. addresses.Select(static a => a.Trim().ToLowerInvariant()).Where(static a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>"adds a, b; removes c", or null when the backend holds exactly what the group asks for (case-insensitive).</summary>
    private static string? Difference(string what, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var want = expected.Select(static a => a.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var have = actual.Select(static a => a.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        string[] missing = [.. want.Except(have).Order(StringComparer.Ordinal)];
        string[] extra = [.. have.Except(want).Order(StringComparer.Ordinal)];
        if (missing.Length == 0 && extra.Length == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (missing.Length > 0)
        {
            parts.Add($"{what} to add: {string.Join(", ", missing)}");
        }

        if (extra.Length > 0)
        {
            parts.Add($"{what} to remove: {string.Join(", ", extra)}");
        }

        return string.Join("; ", parts);
    }
}

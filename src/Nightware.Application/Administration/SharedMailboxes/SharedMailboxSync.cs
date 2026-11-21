using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.SharedMailboxes;

/// <param name="Notes">What could not be done yet and needs nothing from the admin (a member whose own mailbox does not exist yet).</param>
public sealed record SharedMailboxSyncOutcome(SharedMailbox Mailbox, IReadOnlyList<string> Notes);

/// <summary>
/// Derives who may open a shared mailbox from the record (Keycloak) and brings the mail server into line (phase 10).
/// Membership always follows the record; only enabled members are given access, so an offboarded member loses the
/// mailbox and gets it back when reinstated. The mail server matches members by their own mailbox, found by address.
/// </summary>
public sealed class SharedMailboxSync(
    IEntityRegistry entities, IEntityMailboxes mailboxes, ISharedMailAccounts shared, IMailAccounts mail, IGroupwareGroups groupware, ISharedCalendars calendars)
{
    /// <summary>The addresses of the mailboxes that belong in the shared mailbox: the enabled members' own.</summary>
    public static IReadOnlyList<string> MemberAddresses(IEnumerable<IdentityAccount> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Normalise(members.Where(static m => m.Enabled).Select(static m => m.Email));
    }

    public async Task<IReadOnlyList<BackendLink>> InspectAsync(SharedMailbox mailbox, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(members);
        return
        [
            new(BackendKind.Identity, LinkState.Linked, $"{members.Count} member(s).", mailbox.Id),
            await InspectMailAsync(mailbox, members, cancellationToken).ConfigureAwait(false),
            await InspectCalendarAsync(mailbox, members, cancellationToken).ConfigureAwait(false),
        ];
    }

    /// <summary>Creates the shared mailbox when it is missing, then sets its aliases, name and members.</summary>
    public async Task<Result<SharedMailboxSyncOutcome>> ApplyAsync(SharedMailbox mailbox, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(members);
        Result<SharedMailAccount> found = await shared.FindByAddressAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess && found.Error.Kind == ErrorKind.NotFound)
        {
            found = await shared.CreateAsync(mailbox.Address, mailbox.Aliases, mailbox.Name, cancellationToken).ConfigureAwait(false);
        }
        else if (found.IsSuccess && found.Value.IsPrivileged)
        {
            return Privileged(mailbox);
        }
        else if (found.IsSuccess && (!string.Equals(found.Value.Name, mailbox.Name, StringComparison.Ordinal)
            || Difference("aliases", Normalise(mailbox.Aliases.Select(static a => a.Value)), found.Value.Aliases) is not null))
        {
            Result updated = await shared.UpdateAsync(found.Value.Id, mailbox.Aliases, mailbox.Name, cancellationToken).ConfigureAwait(false);
            if (!updated.IsSuccess)
            {
                return updated.Error;
            }
        }

        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var notes = new List<string>();
        IReadOnlyList<string> expected = MemberAddresses(members);
        var current = found.Value.Members.ToDictionary(static m => m.Address.Trim().ToLowerInvariant(), static m => m.AccountId, StringComparer.Ordinal);
        foreach (string address in expected.Where(a => !current.ContainsKey(a)))
        {
            Result<MailAccount> own = await mail.FindByAddressAsync(address, cancellationToken).ConfigureAwait(false);
            if ((!own.IsSuccess && own.Error.Kind == ErrorKind.NotFound) || (own.IsSuccess && !own.Value.IsIndividual))
            {
                notes.Add($"{address} has no mailbox of their own yet; Repair adds them once they have.");
                continue;
            }

            Result added = own.IsSuccess ? await shared.AddMemberAsync(found.Value.Id, own.Value.Id, cancellationToken).ConfigureAwait(false) : own.Error;
            if (!added.IsSuccess)
            {
                return added.Error;
            }
        }

        foreach ((string address, string accountId) in current.Where(c => !expected.Contains(c.Key, StringComparer.Ordinal)))
        {
            Result removed = await shared.RemoveMemberAsync(found.Value.Id, accountId, cancellationToken).ConfigureAwait(false);
            if (!removed.IsSuccess)
            {
                return removed.Error with { Message = $"{address}: {removed.Error.Message}" };
            }
        }

        Result calendar = await ApplyCalendarAsync(mailbox, members, notes, cancellationToken).ConfigureAwait(false);
        return calendar.IsSuccess ? new SharedMailboxSyncOutcome(mailbox, notes) : calendar.Error with { Message = $"Calendar: {calendar.Error.Message}" };
    }

    /// <summary>
    /// The calendar (ADR 0004 D6): the members' Nextcloud group, then the mailbox's own Nextcloud user, its calendar
    /// and the calendar's share with that group. Membership is the group's, so it changes with no calendar call.
    /// </summary>
    private async Task<Result> ApplyCalendarAsync(SharedMailbox mailbox, IReadOnlyList<IdentityAccount> members, List<string> notes, CancellationToken cancellationToken)
    {
        string groupId = mailbox.CalendarGroupId;
        Result<GroupwareGroup> found = await groupware.FindAsync(groupId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> current;
        if (found.IsSuccess)
        {
            current = found.Value.MemberUids;
            if (!string.Equals(found.Value.DisplayName, mailbox.Name, StringComparison.Ordinal)
                && await groupware.SetDisplayNameAsync(groupId, mailbox.Name, cancellationToken).ConfigureAwait(false) is { IsSuccess: false } renamed)
            {
                return renamed;
            }
        }
        else if (found.Error.Kind == ErrorKind.NotFound)
        {
            Result created = await groupware.CreateAsync(groupId, mailbox.Name, cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created;
            }

            current = [];
        }
        else
        {
            return found.Error;
        }

        IReadOnlyList<string> expected = CalendarMembers(members);
        foreach (string uid in expected.Except(current, StringComparer.Ordinal))
        {
            Result added = await groupware.AddMemberAsync(groupId, uid, cancellationToken).ConfigureAwait(false);
            if (!added.IsSuccess && added.Error.Kind == ErrorKind.NotFound)
            {
                notes.Add($"{uid} has not signed in to Nextcloud yet, so has no calendar access; Repair adds them once they have.");
            }
            else if (!added.IsSuccess)
            {
                return added;
            }
        }

        foreach (string uid in current.Except(expected, StringComparer.Ordinal))
        {
            Result removed = await groupware.RemoveMemberAsync(groupId, uid, cancellationToken).ConfigureAwait(false);
            if (!removed.IsSuccess)
            {
                return removed;
            }
        }

        return await calendars.EnsureAsync(mailbox.Address, mailbox.Name, groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Nextcloud uids that may use the calendar: the enabled members' usernames.</summary>
    public static IReadOnlyList<string> CalendarMembers(IEnumerable<IdentityAccount> members) =>
        [.. members.Where(static m => m.Enabled).Select(static m => m.Username).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private async Task<BackendLink> InspectCalendarAsync(SharedMailbox mailbox, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        Result<SharedCalendarAccount> account = await calendars.FindAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Groupware, LinkState.Missing, "No calendar yet; Repair creates it.")
                : new BackendLink(BackendKind.Groupware, LinkState.Error, account.Error.Message);
        }

        Result<GroupwareGroup> group = await groupware.FindAsync(mailbox.CalendarGroupId, cancellationToken).ConfigureAwait(false);
        if (!group.IsSuccess)
        {
            return group.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Groupware, LinkState.Conflict, "The calendar has no members' group; Repair creates it.", account.Value.Uid)
                : new BackendLink(BackendKind.Groupware, LinkState.Error, group.Error.Message);
        }

        // A member who has never signed in to Nextcloud cannot be in the group yet: waiting, not out of step.
        var missing = CalendarMembers(members).Except(group.Value.MemberUids, StringComparer.Ordinal).ToList();
        var extra = group.Value.MemberUids.Except(CalendarMembers(members), StringComparer.Ordinal).ToList();
        return extra.Count > 0
            ? new BackendLink(BackendKind.Groupware, LinkState.Conflict, "The calendar is out of step with the record; Repair brings it into line.", account.Value.Uid)
            : new BackendLink(BackendKind.Groupware, LinkState.Linked, missing.Count == 0 ? "Calendar" : $"Calendar (waiting for {string.Join(", ", missing)} to sign in to Nextcloud)", account.Value.Uid);
    }

    /// <summary>Brings every shared mailbox the user may open back into line (offboarding, reinstating), in every entity they are a member of.</summary>
    public async Task<Result> ApplyForUserAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Result<IReadOnlyList<string>> memberships = await entities.GetMembershipsAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!memberships.IsSuccess)
        {
            return Error.BackendUnavailable($"Shared mailboxes: {memberships.Error.Message}");
        }

        var failures = new List<string>();
        foreach (string entityId in memberships.Value)
        {
            Result<IReadOnlyList<SharedMailbox>> mine = await mailboxes.ListUserMailboxesAsync(entityId, userId, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                failures.Add(mine.Error.Message);
                continue;
            }

            foreach (SharedMailbox mailbox in mine.Value)
            {
                Result<IReadOnlyList<IdentityAccount>> members = await mailboxes.ListMembersAsync(entityId, mailbox.Id, cancellationToken).ConfigureAwait(false);
                Result<SharedMailboxSyncOutcome> applied = members.IsSuccess ? await ApplyAsync(mailbox, members.Value, cancellationToken).ConfigureAwait(false) : members.Error;
                if (!applied.IsSuccess)
                {
                    failures.Add($"{mailbox.Name}: {applied.Error.Message}");
                }
            }
        }

        return failures.Count == 0 ? Result.Success() : Error.BackendUnavailable($"Shared mailboxes: {string.Join(" ", failures)}");
    }

    private async Task<BackendLink> InspectMailAsync(SharedMailbox mailbox, IReadOnlyList<IdentityAccount> members, CancellationToken cancellationToken)
    {
        Result<SharedMailAccount> found = await shared.FindByAddressAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Mail, LinkState.Missing, $"No shared mailbox at {mailbox.Address}.")
                : new BackendLink(BackendKind.Mail, LinkState.Error, found.Error.Message);
        }

        if (found.Value.IsPrivileged)
        {
            return new BackendLink(BackendKind.Mail, LinkState.Conflict, Privileged(mailbox).Message, found.Value.Id);
        }

        // A member whose own mailbox does not exist yet cannot be given this one: waiting, not out of step.
        var expected = MemberAddresses(members).ToList();
        var current = found.Value.Members.Select(static m => m.Address).ToList();
        foreach (string address in expected.Except(current, StringComparer.OrdinalIgnoreCase).ToList())
        {
            Result<MailAccount> own = await mail.FindByAddressAsync(address, cancellationToken).ConfigureAwait(false);
            if (!own.IsSuccess && own.Error.Kind == ErrorKind.NotFound)
            {
                expected.Remove(address);
            }
        }

        string? drift = Difference("members", expected, current)
            ?? Difference("aliases", Normalise(mailbox.Aliases.Select(static a => a.Value)), found.Value.Aliases);
        return drift is null
            ? new BackendLink(BackendKind.Mail, LinkState.Linked, $"{mailbox.Address}", found.Value.Id)
            : new BackendLink(BackendKind.Mail, LinkState.Conflict, $"The shared mailbox is out of step with its record ({drift}); Repair brings it into line.", found.Value.Id);
    }

    private static Error Privileged(SharedMailbox mailbox) =>
        Error.Conflict($"The mail server's account at {mailbox.Address} carries a role or permissions of its own, which its members would gain; the console never gives it members.", "address");

    private static IReadOnlyList<string> Normalise(IEnumerable<string> addresses) =>
        [.. addresses.Select(static a => a.Trim().ToLowerInvariant()).Where(static a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>"members to add: a, b; members to remove: c", or null when the mail server holds exactly what the record asks for (case-insensitive).</summary>
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

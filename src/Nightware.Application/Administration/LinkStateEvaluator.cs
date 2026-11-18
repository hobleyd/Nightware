using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>
/// Derives a user's state in each backend from key lookups (ADR 0002 D3). Pure: the same lookups always
/// give the same links and plan, so a plan shown to the admin is recomputed, never trusted, when applied.
/// A disagreement is a <see cref="LinkState.Conflict"/>; nothing is ever merged automatically.
/// </summary>
public static class LinkStateEvaluator
{
    public static BackendLink Identity(IdentityAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Enabled
            ? new BackendLink(BackendKind.Identity, LinkState.Linked, $"Signs in as {user.Username}.", user.Id)
            : new BackendLink(BackendKind.Identity, LinkState.Disabled, "Offboarded: sign-in is disabled.", user.Id);
    }

    /// <param name="found">The mail server's answer for <paramref name="address"/>.</param>
    /// <param name="administratorExpected">
    /// The address is a global administrator's whose mailbox the console makes a mail-server administrator
    /// (<see cref="AdministrationOptions.GlobalAdminsAdministerServers"/>): that role, and only that, is theirs to hold.
    /// </param>
    public static BackendLink Mail(string address, Result<MailAccount> found, bool administratorExpected = false)
    {
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Mail, LinkState.Missing, $"No mailbox for {address}.")
                : new BackendLink(BackendKind.Mail, LinkState.Error, found.Error.Message);
        }

        MailAccount account = found.Value;
        if (account.IsPrivileged && !(administratorExpected && account.IsAdministrator))
        {
            return new BackendLink(BackendKind.Mail, LinkState.Conflict, account.IsAdministrator
                ? $"{address} is a mail-server administrator but not a global administrator; take the role away in the mail server."
                : $"{address} is a privileged mail-server account; the console never links or deletes it.", account.Id);
        }

        if (!account.IsIndividual)
        {
            return new BackendLink(BackendKind.Mail, LinkState.Conflict, $"{address} belongs to a group or mailing list.", account.Id);
        }

        if (!string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return new BackendLink(BackendKind.Mail, LinkState.Conflict, $"{address} is an alias of the mailbox {account.Address}.", account.Id);
        }

        return account.Enabled
            ? new BackendLink(BackendKind.Mail, LinkState.Linked, account.IsAdministrator ? $"Mailbox {account.Address} (mail-server administrator)" : $"Mailbox {account.Address}", account.Id)
            : new BackendLink(BackendKind.Mail, LinkState.Disabled, $"Mailbox {account.Address}: sign-in blocked, mail still delivered.", account.Id);
    }

    /// <param name="uid">The Nextcloud uid sign-in would use (the Keycloak username).</param>
    /// <param name="administratorExpected">
    /// The uid is a global administrator's, whom the console puts in Nextcloud's <c>admin</c> group
    /// (<see cref="AdministrationOptions.GlobalAdminsAdministerServers"/>).
    /// </param>
    public static BackendLink Groupware(string uid, string email, Result<GroupwareAccount> found, bool administratorExpected = false)
    {
        if (!found.IsSuccess)
        {
            return found.Error.Kind == ErrorKind.NotFound
                ? new BackendLink(BackendKind.Groupware, LinkState.Missing, $"No account for {uid}.")
                : new BackendLink(BackendKind.Groupware, LinkState.Error, found.Error.Message);
        }

        GroupwareAccount account = found.Value;
        if (account.IsAdministrator && !(administratorExpected && account.IsAdminGroupMember))
        {
            return new BackendLink(BackendKind.Groupware, LinkState.Conflict, account.IsAdminGroupMember
                ? $"{uid} is a Nextcloud administrator but not a global administrator; take them out of Nextcloud's admin group."
                : $"{uid} is a Nextcloud administrator; the console never links or manages it.", account.Uid);
        }

        if (!account.ManagedByIdentityProvider)
        {
            return new BackendLink(BackendKind.Groupware, LinkState.Conflict, $"{uid} is a local Nextcloud account, which Keycloak sign-in would not use.", account.Uid);
        }

        if (!string.IsNullOrWhiteSpace(account.Email) && !string.Equals(account.Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            return new BackendLink(BackendKind.Groupware, LinkState.Conflict, $"Nextcloud user {uid} has the address {account.Email}.", account.Uid);
        }

        return account.Enabled
            ? new BackendLink(BackendKind.Groupware, LinkState.Linked, account.IsAdminGroupMember ? $"Account {uid} (Nextcloud administrator)." : $"Account {uid}.", account.Uid)
            : new BackendLink(BackendKind.Groupware, LinkState.Disabled, $"Account {uid} is disabled.", account.Uid);
    }

    /// <summary>The identity step of a provisioning plan; also yields the existing user to link, if any.</summary>
    /// <param name="byEmail">Keycloak's answer for the requested address.</param>
    /// <param name="byUsername">Keycloak's answer for the requested (or proposed) username.</param>
    /// <param name="existingMemberships">Entities the user found by e-mail belongs to.</param>
    public static (PlannedStep Step, IdentityAccount? Existing) IdentityStep(
        string entityId,
        string? requestedUsername,
        string proposedUsername,
        Result<IdentityAccount> byEmail,
        Result<IdentityAccount> byUsername,
        IReadOnlyList<string> existingMemberships)
    {
        ArgumentNullException.ThrowIfNull(existingMemberships);
        if (byEmail.IsSuccess)
        {
            IdentityAccount existing = byEmail.Value;
            if (requestedUsername is not null && !string.Equals(existing.Username, requestedUsername, StringComparison.OrdinalIgnoreCase))
            {
                return (Conflict(BackendKind.Identity, $"The address already belongs to Keycloak user {existing.Username}, not {requestedUsername}."), null);
            }

            if (!existing.Enabled)
            {
                return (Conflict(BackendKind.Identity, $"Keycloak user {existing.Username} is offboarded; reinstate them from their page instead."), null);
            }

            if (existingMemberships.Count > 0 && !existingMemberships.Contains(entityId, StringComparer.Ordinal))
            {
                return (Conflict(BackendKind.Identity, $"Keycloak user {existing.Username} belongs to another entity."), null);
            }

            string membership = existingMemberships.Count == 0 ? "; will be added to the entity" : string.Empty;
            return (new PlannedStep(BackendKind.Identity, PlannedAction.Link, $"Link to existing Keycloak user {existing.Username}{membership}; their password is not changed."), existing);
        }

        if (byUsername.IsSuccess)
        {
            return (Conflict(BackendKind.Identity, $"The username {proposedUsername} is taken by the user with address {byUsername.Value.Email}."), null);
        }

        return (new PlannedStep(BackendKind.Identity, PlannedAction.Create, $"Create Keycloak user {proposedUsername}."), null);
    }

    /// <summary>The mail or groupware step of a plan from that backend's link state.</summary>
    public static PlannedStep Step(BackendLink link, string createReason)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.State switch
        {
            LinkState.Missing => new PlannedStep(link.Backend, PlannedAction.Create, createReason),
            LinkState.Linked => new PlannedStep(link.Backend, PlannedAction.Link, $"Link to existing: {link.Detail}"),
            LinkState.Disabled => new PlannedStep(link.Backend, PlannedAction.Update, $"Re-enable: {link.Detail}"),
            LinkState.Pending => new PlannedStep(link.Backend, PlannedAction.Skip, link.Detail ?? "Created at first sign-in."),
            _ => Conflict(link.Backend, link.Detail ?? "The backend could not be checked."),
        };
    }

    private static PlannedStep Conflict(BackendKind backend, string reason) => new(backend, PlannedAction.Conflict, reason);
}

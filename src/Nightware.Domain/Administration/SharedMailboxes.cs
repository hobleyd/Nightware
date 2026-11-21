namespace Nightware.Domain.Administration;

/// <summary>
/// A mailbox several of an entity's users open (phase 10): one inbox, its own sent items, its own address. Kept in
/// Keycloak (an organization group, named by <see cref="Address"/>, whose members are the people who may open it) and
/// mirrored to a mail server group account whose members are the same people.
/// </summary>
/// <param name="Address">Never changes after creation: it names the mailbox everywhere.</param>
public sealed record SharedMailbox(string Id, string EntityId, MailAddress Address, string Name)
{
    public IReadOnlyList<MailAddress> Aliases { get; init; } = [];

    /// <summary>The address and its aliases.</summary>
    public IEnumerable<MailAddress> Addresses => Aliases.Prepend(Address);

    /// <summary>
    /// The Nextcloud group of its members, which its calendar is shared with (ADR 0004 D6). A Nextcloud uid and a group
    /// id are separate namespaces; the calendar's own user has the same name.
    /// </summary>
    public string CalendarGroupId => "mbx-" + Address.Value.ToLowerInvariant();
}

/// <summary>What the console asks for when a shared mailbox is created.</summary>
public sealed record NewSharedMailbox(MailAddress Address, string Name)
{
    public IReadOnlyList<MailAddress> Aliases { get; init; } = [];
}

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// How the console lays out an entity's groups and shared mailboxes among the organization's Keycloak groups (ADR 0003,
/// ADR 0004): the console writes them and the façade reads their addresses, so both use these names.
/// </summary>
internal static class OrganizationGroupNames
{
    /// <summary>The top-level organization group whose subgroups are the entity's groups.</summary>
    public const string GroupsParent = "groups";

    /// <summary>The top-level organization group whose subgroups are the entity's shared mailboxes.</summary>
    public const string MailboxesParent = "mailboxes";

    /// <summary>A group's or shared mailbox's display name.</summary>
    public const string NameAttribute = "nightware_name";

    /// <summary>A group's list address; a group without one has no mail.</summary>
    public const string ListAddressAttribute = "nightware_list_address";

    /// <summary>Who may send to a group's list: <c>members</c> (the default), <c>organization</c> or <c>anyone</c>.</summary>
    public const string SendersAttribute = "nightware_senders";
}

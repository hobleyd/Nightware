using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>The signed-in administrator for this request (host-provided, ADR 0002 D6).</summary>
public interface IAdminActor
{
    AdminActor Current { get; }
}

/// <summary>Re-resolves an administrator's scope from the system of record (ADR 0002 D1).</summary>
public interface IAdminScopeSource
{
    Task<Result<AdminScope>> ResolveAsync(string userId, CancellationToken cancellationToken);
}

/// <summary>Entities (Keycloak Organizations), their domains, members and entity admins.</summary>
public interface IEntityRegistry
{
    Task<Result<IReadOnlyList<Entity>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<Entity>> GetAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Conflict when the alias or the domain is already taken.</summary>
    Task<Result<Entity>> CreateAsync(NewEntity entity, CancellationToken cancellationToken);

    /// <summary>Conflict when another entity owns the domain; adding a domain the entity has is a no-op.</summary>
    Task<Result<Entity>> AddDomainAsync(string entityId, MailDomain domain, CancellationToken cancellationToken);

    /// <summary>Also forgets the domain's SPF and DMARC choice and its catch-all.</summary>
    Task<Result<Entity>> RemoveDomainAsync(string entityId, MailDomain domain, CancellationToken cancellationToken);

    /// <summary>Stores the domain's SPF and DMARC choice (a null record returns it to the mail server's default). NotFound when the entity lacks the domain.</summary>
    Task<Result<Entity>> SetMailPolicyAsync(string entityId, MailDomain domain, DomainMailPolicy policy, CancellationToken cancellationToken);

    /// <summary>
    /// Records the user (by id) who receives the domain's mail to unknown addresses; with null, that mail is refused.
    /// NotFound when the entity lacks the domain.
    /// </summary>
    Task<Result<Entity>> SetCatchAllAsync(string entityId, MailDomain domain, string? userId, CancellationToken cancellationToken);

    /// <summary>The entity's sealed DNS host credential (<see cref="ISecretSealer"/>) and its host, or null when it has none.</summary>
    Task<Result<SealedDnsToken?>> GetSealedDnsTokenAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Stores the entity's sealed DNS host credential, forgetting any other host's; with null removes every one.</summary>
    Task<Result> SetSealedDnsTokenAsync(string entityId, SealedDnsToken? token, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Ids of the entities <paramref name="userId"/> is a member of.</summary>
    Task<Result<IReadOnlyList<string>>> GetMembershipsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> AddMemberAsync(string entityId, string userId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAccount>>> ListAdminsAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> GrantAdminAsync(string entityId, string userId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> RevokeAdminAsync(string entityId, string userId, CancellationToken cancellationToken);
}

public sealed record NewIdentity(string Username, MailAddress Email, string? FirstName, string? LastName, IReadOnlyList<RequiredAction> RequiredActions);

/// <summary>Users in the identity provider (Keycloak), managed with the console's own service account.</summary>
public interface IIdentityAccounts
{
    Task<Result<IdentityAccount>> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>NotFound when no user has exactly this address.</summary>
    Task<Result<IdentityAccount>> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>NotFound when no user has exactly this username.</summary>
    Task<Result<IdentityAccount>> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>Created enabled, with a verified e-mail address and the given required actions.</summary>
    Task<Result<IdentityAccount>> CreateAsync(NewIdentity identity, CancellationToken cancellationToken);

    Task<Result> SetNameAsync(string userId, string? firstName, string? lastName, CancellationToken cancellationToken);

    Task<Result> SetAliasesAsync(string userId, IReadOnlyList<MailAddress> aliases, CancellationToken cancellationToken);

    /// <summary>Replaces the user's Graph profile fields (blank values clear them).</summary>
    Task<Result> SetProfileAsync(string userId, UserProfile profile, CancellationToken cancellationToken);

    /// <summary>Sets the user's manager to <paramref name="managerId"/>, or removes it when null.</summary>
    Task<Result> SetManagerAsync(string userId, string? managerId, CancellationToken cancellationToken);

    Task<Result> SetEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the account's email verified: the façade refuses tokens without <c>email_verified</c>, since the email
    /// names the mailbox. Linking an account to its mailbox is that verification.
    /// </summary>
    Task<Result> MarkEmailVerifiedAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Signs the user out of every session.</summary>
    Task<Result> EndSessionsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Keycloak e-mails the user a link that performs <paramref name="actions"/>.</summary>
    Task<Result> SendActionEmailAsync(string userId, IReadOnlyList<RequiredAction> actions, TimeSpan lifespan, CancellationToken cancellationToken);

    /// <summary>Sets a temporary password that must be changed at the next sign-in.</summary>
    Task<Result> SetTemporaryPasswordAsync(string userId, TemporaryCredential credential, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> AddRequiredActionAsync(string userId, RequiredAction action, CancellationToken cancellationToken);

    /// <summary>
    /// Ranks the user's newest passkey first among their credentials, so the second sign-in step asks for it
    /// rather than an authenticator code. NotFound when they have no passkey. Idempotent.
    /// </summary>
    Task<Result> PreferNewestPasskeyAsync(string userId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAccount>>> ListGlobalAdminsAsync(CancellationToken cancellationToken);

    Task<Result<bool>> IsGlobalAdminAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> SetGlobalAdminAsync(string userId, bool granted, CancellationToken cancellationToken);

    /// <summary>Records (or with null clears) a deletion marker: <see cref="IdentityAccount.MailDeletedAt"/> or <see cref="IdentityAccount.GroupwareDeletedAt"/>.</summary>
    Task<Result> SetDeletionMarkerAsync(string userId, DeletionTarget target, DateTimeOffset? at, CancellationToken cancellationToken);

    /// <summary>Puts <paramref name="added"/> first in the user's access history, keeping the last <see cref="AccessEvent.Keep"/>.</summary>
    Task<Result> AddAccessEventAsync(string userId, AccessEvent added, CancellationToken cancellationToken);
}

/// <param name="IsIndividual">False for a shared mailbox or mailing list that holds the address.</param>
public sealed record MailAccount(string Id, string Address, bool IsIndividual)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>A shared mailbox (phase 10) holds the address: not individual, and not a list.</summary>
    public bool IsSharedMailbox { get; init; }

    /// <summary>
    /// Holds more than an ordinary mailbox's rights (an administrator, or the console's own principal). Never
    /// linked to a person or deleted by the console: whoever signed in as that address would gain them, unless
    /// it is a global administrator's mailbox holding exactly <see cref="IsAdministrator"/>
    /// (<see cref="AdministrationOptions.GlobalAdminsAdministerServers"/>).
    /// </summary>
    public bool IsPrivileged { get; init; }

    /// <summary>
    /// Holds the mail server's administrator role and nothing else beyond an ordinary mailbox: the role the
    /// console gives a global administrator's mailbox. Implies <see cref="IsPrivileged"/>.
    /// </summary>
    public bool IsAdministrator { get; init; }

    /// <summary>False when sign-in to the mail server is blocked (an offboarded user); mail is still delivered.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Mail accounts on the mail server (Stalwart), managed with the console's own principal.</summary>
public interface IMailAccounts
{
    /// <summary>The account whose primary address or alias is <paramref name="address"/>; NotFound when none.</summary>
    Task<Result<MailAccount>> FindByAddressAsync(string address, CancellationToken cancellationToken);

    Task<Result<MailAccount>> CreateAsync(MailAddress address, string description, CancellationToken cancellationToken);

    Task<Result> SetAliasesAsync(string accountId, IReadOnlyList<MailAddress> aliases, CancellationToken cancellationToken);

    /// <summary>Blocks or restores every way of signing in to the mailbox (app passwords included). Delivery continues.</summary>
    Task<Result> SetEnabledAsync(string accountId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Gives the mailbox the mail server's administrator role, or returns it to an ordinary user's. Forbidden unless
    /// the console's own principal is a mail-server administrator.
    /// </summary>
    Task<Result> SetAdministratorAsync(string accountId, bool administrator, CancellationToken cancellationToken);

    /// <summary>Bytes stored.</summary>
    Task<Result<long>> GetUsageAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>Destroys the account and its mail; NotFound when it is already gone.</summary>
    Task<Result> DestroyAsync(string accountId, CancellationToken cancellationToken);
}

public sealed record MailDomainInfo(string Id, MailDomain Domain, bool DkimPresent)
{
    /// <summary>Where the mail server sends mail to unknown addresses on the domain; null when it refuses it.</summary>
    public string? CatchAllAddress { get; init; }
}

/// <summary>Domains on the mail server (Stalwart).</summary>
public interface IMailDomains
{
    /// <summary>NotFound when the mail server does not serve the domain.</summary>
    Task<Result<MailDomainInfo>> FindAsync(MailDomain domain, CancellationToken cancellationToken);

    /// <summary>Creates the domain with automatic DKIM keys.</summary>
    Task<Result<MailDomainInfo>> CreateAsync(MailDomain domain, string description, CancellationToken cancellationToken);

    /// <summary>
    /// Destroys the domain with its DKIM keys, so the mail server stops treating it as local. NotFound when it is
    /// already gone; Conflict, with nothing destroyed, while any account has an address or alias on it.
    /// </summary>
    Task<Result> DestroyAsync(MailDomain domain, CancellationToken cancellationToken);

    /// <summary>
    /// Sends mail to unknown addresses on the domain to <paramref name="address"/>, or with null refuses it again.
    /// Takes effect at once. NotFound when the mail server does not serve the domain.
    /// </summary>
    Task<Result> SetCatchAllAsync(MailDomain domain, MailAddress? address, CancellationToken cancellationToken);

    /// <summary>Reloads the mail server's settings, which loads the DKIM keys of a domain created without a reload.</summary>
    Task<Result> ReloadSettingsAsync(CancellationToken cancellationToken);

    /// <summary>The records the domain needs in public DNS (MX, SPF, DKIM, DMARC, …).</summary>
    Task<Result<IReadOnlyList<DnsRecord>>> GetDnsRecordsAsync(MailDomain domain, CancellationToken cancellationToken);
}

/// <param name="ManagedByIdentityProvider">The account signs in through Keycloak (Nextcloud <c>user_oidc</c>), not a local password.</param>
public sealed record GroupwareAccount(string Uid, string? Email, string? DisplayName, bool Enabled, bool ManagedByIdentityProvider)
{
    /// <summary>
    /// A Nextcloud administrator or group sub-administrator. Never linked or managed by the console, unless it is a
    /// global administrator's account in the <c>admin</c> group (<see cref="IsAdminGroupMember"/>,
    /// <see cref="AdministrationOptions.GlobalAdminsAdministerServers"/>).
    /// </summary>
    public bool IsAdministrator { get; init; }

    /// <summary>In Nextcloud's <c>admin</c> group, the role the console gives a global administrator. Implies <see cref="IsAdministrator"/>.</summary>
    public bool IsAdminGroupMember { get; init; }
}

/// <summary>Accounts on the groupware server (Nextcloud), managed with the console's own credential.</summary>
public interface IGroupwareAccounts
{
    /// <summary>NotFound when there is no user with that uid.</summary>
    Task<Result<GroupwareAccount>> FindAsync(string uid, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the user ahead of first sign-in, as sign-in would. Unsupported when the server cannot
    /// pre-provision; the user is then created at first sign-in (<see cref="LinkState.Pending"/>).
    /// </summary>
    Task<Result> PreProvisionAsync(string uid, string email, string displayName, CancellationToken cancellationToken);

    Task<Result> SetEnabledAsync(string uid, bool enabled, CancellationToken cancellationToken);

    /// <summary>Adds the user to Nextcloud's <c>admin</c> group, or takes them out of it. NotFound when there is no such user.</summary>
    Task<Result> SetAdministratorAsync(string uid, bool administrator, CancellationToken cancellationToken);

    /// <summary>Deletes the user and every file, calendar and contact they own; NotFound when already gone.</summary>
    Task<Result> DeleteAsync(string uid, CancellationToken cancellationToken);
}

/// <summary>An entity's groups (phase 9), kept in the identity provider (Keycloak organization groups) as the record.</summary>
public interface IEntityGroups
{
    Task<Result<IReadOnlyList<Group>>> ListAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>NotFound when the entity has no such group.</summary>
    Task<Result<Group>> GetAsync(string entityId, string groupId, CancellationToken cancellationToken);

    /// <summary>Conflict when the entity has a group with that key.</summary>
    Task<Result<Group>> CreateAsync(string entityId, NewGroup group, CancellationToken cancellationToken);

    /// <summary>Stores the name and both facets; the key never changes.</summary>
    Task<Result<Group>> UpdateAsync(Group group, CancellationToken cancellationToken);

    /// <summary>NotFound when already gone.</summary>
    Task<Result> DeleteAsync(string entityId, string groupId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, string groupId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> AddMemberAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> RemoveMemberAsync(string entityId, string groupId, string userId, CancellationToken cancellationToken);

    /// <summary>The groups of <paramref name="entityId"/> that <paramref name="userId"/> belongs to.</summary>
    Task<Result<IReadOnlyList<Group>>> ListUserGroupsAsync(string entityId, string userId, CancellationToken cancellationToken);
}

/// <summary>A distribution list on the mail server: the addresses it answers to and the addresses it delivers to.</summary>
public sealed record MailingList(string Id, string Address)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<string> Recipients { get; init; } = [];
}

/// <summary>Distribution lists on the mail server (Stalwart), and who may send to them.</summary>
public interface IMailingLists
{
    /// <summary>The list whose address is <paramref name="address"/>; NotFound when none.</summary>
    Task<Result<MailingList>> FindByAddressAsync(MailAddress address, CancellationToken cancellationToken);

    /// <summary>Conflict, naming the holder, when a mailbox, alias or other list has any of the addresses.</summary>
    Task<Result<MailingList>> CreateAsync(MailAddress address, IReadOnlyList<MailAddress> aliases, IReadOnlyList<string> recipients, string description, CancellationToken cancellationToken);

    /// <summary>Replaces the aliases and recipients. Conflict, naming the holder, when an alias is taken.</summary>
    Task<Result> UpdateAsync(string listId, IReadOnlyList<MailAddress> aliases, IReadOnlyList<string> recipients, CancellationToken cancellationToken);

    /// <summary>NotFound when already gone.</summary>
    Task<Result> DestroyAsync(string listId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the mail server refuse mail to <paramref name="list"/> from senders <paramref name="rule"/> does not allow;
    /// with null, lets anyone send. Takes effect at once (the server's settings are reloaded). Idempotent.
    /// </summary>
    Task<Result> SetSenderRuleAsync(MailAddress list, SenderRule? rule, CancellationToken cancellationToken);
}

public sealed record GroupwareGroup(string Id, string? DisplayName, IReadOnlyList<string> MemberUids);

/// <param name="Groups">The groups the folder is shared with, by groupware group id.</param>
public sealed record TeamFolder(int Id, string MountPoint, IReadOnlySet<string> Groups);

/// <summary>A shared mailbox's calendar account in Nextcloud.</summary>
public sealed record SharedCalendarAccount(string Uid, string? DisplayName);

/// <summary>
/// A shared mailbox's calendar (ADR 0004 D6): a Nextcloud user of its own, carrying the mailbox's address (so the
/// façade's principal lookup finds it) and nobody's sign-in, whose <c>personal</c> calendar is shared read-write
/// with a Nextcloud group of the mailbox's members (<see cref="IGroupwareGroups"/>).
/// </summary>
public interface ISharedCalendars
{
    /// <summary>NotFound when the mailbox has no Nextcloud user.</summary>
    Task<Result<SharedCalendarAccount>> FindAsync(MailAddress address, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or repairs the user, its calendar and address book, and their shares with <paramref name="groupId"/>.
    /// Idempotent.
    /// </summary>
    Task<Result> EnsureAsync(MailAddress address, string displayName, string groupId, CancellationToken cancellationToken);

    /// <summary>Deletes the user and so the calendar and its events. NotFound when already gone.</summary>
    Task<Result> DeleteAsync(MailAddress address, CancellationToken cancellationToken);
}

/// <summary>Groups and team folders on the groupware server (Nextcloud), managed with the console's own credential.</summary>
public interface IGroupwareGroups
{
    /// <summary>NotFound when there is no group with that id.</summary>
    Task<Result<GroupwareGroup>> FindAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>Conflict when the id is taken.</summary>
    Task<Result> CreateAsync(string groupId, string displayName, CancellationToken cancellationToken);

    Task<Result> SetDisplayNameAsync(string groupId, string displayName, CancellationToken cancellationToken);

    /// <summary>NotFound when there is no user <paramref name="uid"/> (not signed in yet, and not pre-provisioned).</summary>
    Task<Result> AddMemberAsync(string groupId, string uid, CancellationToken cancellationToken);

    Task<Result> RemoveMemberAsync(string groupId, string uid, CancellationToken cancellationToken);

    /// <summary>Team folders shared with the group are kept, shared with no one. NotFound when already gone.</summary>
    Task<Result> DeleteAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>NotFound when there is no team folder with that id.</summary>
    Task<Result<TeamFolder>> FindFolderAsync(int folderId, CancellationToken cancellationToken);

    Task<Result<TeamFolder>> CreateFolderAsync(string mountPoint, CancellationToken cancellationToken);

    /// <summary>Shares the folder with the group to read, change, add and delete (not re-share). Idempotent.</summary>
    Task<Result> GrantFolderAsync(int folderId, string groupId, CancellationToken cancellationToken);

    /// <summary>Deletes the folder and every file in it. NotFound when already gone.</summary>
    Task<Result> DeleteFolderAsync(int folderId, CancellationToken cancellationToken);
}

/// <summary>An entity's shared mailboxes (phase 10), kept in the identity provider (Keycloak organization groups) as the record.</summary>
public interface IEntityMailboxes
{
    Task<Result<IReadOnlyList<SharedMailbox>>> ListAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>NotFound when the entity has no such shared mailbox.</summary>
    Task<Result<SharedMailbox>> GetAsync(string entityId, string mailboxId, CancellationToken cancellationToken);

    /// <summary>Conflict when the entity already has a shared mailbox at that address.</summary>
    Task<Result<SharedMailbox>> CreateAsync(string entityId, NewSharedMailbox mailbox, CancellationToken cancellationToken);

    /// <summary>Stores the name and aliases; the address never changes.</summary>
    Task<Result<SharedMailbox>> UpdateAsync(SharedMailbox mailbox, CancellationToken cancellationToken);

    /// <summary>NotFound when already gone.</summary>
    Task<Result> DeleteAsync(string entityId, string mailboxId, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAccount>>> ListMembersAsync(string entityId, string mailboxId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> AddMemberAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken);

    /// <summary>Idempotent.</summary>
    Task<Result> RemoveMemberAsync(string entityId, string mailboxId, string userId, CancellationToken cancellationToken);

    /// <summary>The shared mailboxes of <paramref name="entityId"/> that <paramref name="userId"/> may open.</summary>
    Task<Result<IReadOnlyList<SharedMailbox>>> ListUserMailboxesAsync(string entityId, string userId, CancellationToken cancellationToken);
}

/// <summary>A mailbox account a shared mailbox's member can open, by its mail server id and its primary address.</summary>
public sealed record SharedMailMember(string AccountId, string Address);

/// <summary>A shared mailbox on the mail server: the addresses it answers to and the accounts that may open it.</summary>
public sealed record SharedMailAccount(string Id, string Address)
{
    /// <summary>The name its mail is sent under (new identities take it; existing ones keep theirs).</summary>
    public string? Name { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<SharedMailMember> Members { get; init; } = [];

    /// <summary>
    /// Carries a role or permissions of its own, which every member would gain: never given members by the console.
    /// </summary>
    public bool IsPrivileged { get; init; }
}

/// <summary>Shared mailboxes on the mail server (Stalwart group accounts), managed with the console's own principal.</summary>
public interface ISharedMailAccounts
{
    /// <summary>The shared mailbox whose primary address is <paramref name="address"/>; NotFound when none.</summary>
    Task<Result<SharedMailAccount>> FindByAddressAsync(MailAddress address, CancellationToken cancellationToken);

    /// <summary>Conflict, naming the holder, when a mailbox, alias or list has any of the addresses.</summary>
    Task<Result<SharedMailAccount>> CreateAsync(MailAddress address, IReadOnlyList<MailAddress> aliases, string name, CancellationToken cancellationToken);

    /// <summary>Replaces the aliases and the name. Conflict, naming the holder, when an alias is taken.</summary>
    Task<Result> UpdateAsync(string mailboxId, IReadOnlyList<MailAddress> aliases, string name, CancellationToken cancellationToken);

    /// <summary>Lets the mail account <paramref name="memberAccountId"/> open the shared mailbox and send as it. Idempotent.</summary>
    Task<Result> AddMemberAsync(string mailboxId, string memberAccountId, CancellationToken cancellationToken);

    /// <summary>Takes that access away at once. Idempotent.</summary>
    Task<Result> RemoveMemberAsync(string mailboxId, string memberAccountId, CancellationToken cancellationToken);

    /// <summary>Takes every member out, then destroys the shared mailbox and its mail. NotFound when already gone.</summary>
    Task<Result> DestroyAsync(string mailboxId, CancellationToken cancellationToken);
}

public sealed record BackendHealth(BackendKind Backend, bool Healthy, string? Detail = null);

/// <summary>Whether the console can reach and authenticate to one backend (one implementation per backend).</summary>
public interface IBackendStatus
{
    Task<BackendHealth> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>One audited administrative action (ADR 0002 D11). Never carries a secret.</summary>
public sealed record AuditEvent(string Action, string ActorId, bool Succeeded)
{
    public string? TargetUserId { get; init; }

    public string? EntityId { get; init; }

    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
}

public interface IAuditLog
{
    void Record(AuditEvent auditEvent);
}

/// <summary>
/// Encrypts the secrets the console keeps in the system of record (host-provided). A secret sealed for one
/// <c>purpose</c> (an entity's id is part of it) cannot be unsealed for another.
/// </summary>
public interface ISecretSealer
{
    string Seal(string purpose, string secret);

    /// <summary>False when the sealed text is not readable (tampered, or the key it was sealed with is gone).</summary>
    bool TryUnseal(string purpose, string sealedSecret, out string secret);
}

/// <summary>An entity's DNS host credential as stored: sealed (<see cref="ISecretSealer"/>), with the host it is for.</summary>
public sealed record SealedDnsToken(DnsProvider Provider, string Sealed);

/// <summary>
/// One DNS host (<see cref="Provider"/>; one implementation each), reached with the entity's own credential. A
/// credential the host refuses is a <see cref="ErrorKind.Validation"/> error with target <c>dnsToken</c>. Zone
/// records carry ids that stay the same while the record does, since a plan's fingerprint includes them.
/// </summary>
public interface IDnsHosting
{
    DnsProvider Provider { get; }

    /// <summary>The zone holding <paramref name="domain"/>: the domain's own or its closest parent the token can see; NotFound when none.</summary>
    Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken);

    /// <summary>Makes one <see cref="DnsChange.IsWrite"/> change; records are written as plain DNS (never proxied).</summary>
    Task<Result> ApplyAsync(DnsProviderToken token, DnsZone zone, DnsChange change, CancellationToken cancellationToken);
}

/// <summary>
/// Public DNS, asked from outside the deployment's own network: inside it, the public host name resolves to the
/// reverse proxy's private address. A lookup that fails or times out is an error for the caller to treat as
/// "not known", never as a page failure.
/// </summary>
public interface IPublicDns
{
    /// <summary>The host's A and AAAA addresses, as public resolvers answer them.</summary>
    Task<Result<IReadOnlyList<System.Net.IPAddress>>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface IPasswordGenerator
{
    TemporaryCredential Generate();
}

/// <summary>Console policy settings the host binds from configuration (<c>Admin:</c>).</summary>
public sealed class AdministrationOptions
{
    /// <summary>How recent the actor's sign-in must be for a destructive action.</summary>
    public TimeSpan DestructiveActionMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Lets entity admins delete data of their own entity's disabled users (default off, ADR 0002 OQ2b).</summary>
    public bool EntityAdminsMayDeleteData { get; set; }

    /// <summary>
    /// Global administrators also administer Keycloak's realm, the mail server and Nextcloud (ADR 0002 D6,
    /// amended): their role carries Keycloak's <c>realm-admin</c> (keycloak/configure-console.sh), granting or
    /// revoking it sets their mailbox's administrator role and their Nextcloud <c>admin</c> group membership, and a
    /// global admin may manage another global admin although that account administers those servers, since the
    /// actor holds the same rights already. Off by default: the console's mail principal must itself be a
    /// mail-server administrator to grant the role.
    /// </summary>
    public bool GlobalAdminsAdministerServers { get; set; }

    /// <summary><c>acr</c> values that count as strong authentication; empty accepts any (non-production only).</summary>
    public IList<string> AcceptedAcrValues { get; } = [];

    /// <summary>How long a set-password e-mail link stays valid.</summary>
    public TimeSpan ActionEmailLifespan { get; set; } = TimeSpan.FromDays(3);

    /// <summary>
    /// The realm's <c>accessTokenLifespan</c>: how long a token issued before an offboarding keeps working,
    /// which the console tells the admin.
    /// </summary>
    public TimeSpan AccessTokenLifespan { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The public host name the stack is served at (<c>PUBLIC_HOST</c>); its public addresses are the ones the
    /// suggested SPF record lets send. Null leaves the suggestion on the domain's MX hosts.
    /// </summary>
    public string? PublicHost { get; set; }

    /// <summary>
    /// Local parts never given to a person, on any domain: the console's own mail principal, the realm's
    /// sender, Nextcloud's invitation sender, and the role addresses mail systems rely on.
    /// </summary>
    public ISet<string> ReservedLocalParts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nightware-admin", "__calendar__", "noreply", "no-reply", "calendar", "postmaster", "abuse", "hostmaster", "webmaster", "mailer-daemon", "root",
    };
}

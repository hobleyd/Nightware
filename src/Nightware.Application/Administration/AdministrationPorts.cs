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

    /// <summary>Also forgets the domain's SPF and DMARC choice.</summary>
    Task<Result<Entity>> RemoveDomainAsync(string entityId, MailDomain domain, CancellationToken cancellationToken);

    /// <summary>Stores the domain's SPF and DMARC choice (a null record returns it to the mail server's default). NotFound when the entity lacks the domain.</summary>
    Task<Result<Entity>> SetMailPolicyAsync(string entityId, MailDomain domain, DomainMailPolicy policy, CancellationToken cancellationToken);

    /// <summary>The entity's sealed DNS host token (<see cref="ISecretSealer"/>), or null when it has none.</summary>
    Task<Result<string?>> GetSealedDnsTokenAsync(string entityId, CancellationToken cancellationToken);

    /// <summary>Stores the entity's sealed DNS host token, or with null removes it.</summary>
    Task<Result> SetSealedDnsTokenAsync(string entityId, string? sealedToken, CancellationToken cancellationToken);

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

    Task<Result> SetEnabledAsync(string userId, bool enabled, CancellationToken cancellationToken);

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

/// <param name="IsIndividual">False for a group or mailing list that holds the address.</param>
public sealed record MailAccount(string Id, string Address, bool IsIndividual)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];

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

public sealed record MailDomainInfo(string Id, MailDomain Domain, bool DkimPresent);

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

    /// <summary>Reloads the mail server's settings, which loads the DKIM keys of a domain created without a reload.</summary>
    Task<Result> ReloadSettingsAsync(CancellationToken cancellationToken);

    /// <summary>The records the domain needs in public DNS (MX, SPF, DKIM, DMARC, …).</summary>
    Task<Result<IReadOnlyList<DnsRecord>>> GetDnsRecordsAsync(MailDomain domain, CancellationToken cancellationToken);
}

/// <param name="ManagedByIdentityProvider">The account signs in through Keycloak (Nextcloud <c>user_oidc</c>), not a local password.</param>
public sealed record GroupwareAccount(string Uid, string? Email, string? DisplayName, bool Enabled, bool ManagedByIdentityProvider)
{
    /// <summary>A Nextcloud administrator or group sub-administrator. Never linked or managed by the console.</summary>
    public bool IsAdministrator { get; init; }
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

    /// <summary>Deletes the user and every file, calendar and contact they own; NotFound when already gone.</summary>
    Task<Result> DeleteAsync(string uid, CancellationToken cancellationToken);
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

/// <summary>
/// An entity's DNS host (Cloudflare), reached with the entity's own token. A token the host refuses is a
/// <see cref="ErrorKind.Validation"/> error with target <c>dnsToken</c>.
/// </summary>
public interface IDnsHosting
{
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
    /// Global administrators also administer Keycloak's realm and the mail server (ADR 0002 D6, amended): their
    /// role carries Keycloak's <c>realm-admin</c> (keycloak/configure-console.sh), granting or revoking it sets
    /// their mailbox's administrator role, and a global admin may manage another global admin although that
    /// account administers those servers, since the actor holds the same rights already. Off by default: the
    /// console's mail principal must itself be a mail-server administrator to grant the role.
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
    /// sender, and the role addresses mail systems rely on.
    /// </summary>
    public ISet<string> ReservedLocalParts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nightware-admin", "noreply", "no-reply", "calendar", "postmaster", "abuse", "hostmaster", "webmaster", "mailer-daemon", "root",
    };
}

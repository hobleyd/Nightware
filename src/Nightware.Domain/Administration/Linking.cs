namespace Nightware.Domain.Administration;

/// <summary>The three systems a person exists in (ADR 0002 D3).</summary>
public enum BackendKind
{
    /// <summary>Keycloak.</summary>
    Identity,

    /// <summary>Stalwart.</summary>
    Mail,

    /// <summary>Nextcloud.</summary>
    Groupware,
}

/// <summary>A user's state in one backend, derived on read by matching keys; never stored.</summary>
public enum LinkState
{
    /// <summary>The account exists and its keys agree with Keycloak.</summary>
    Linked,

    /// <summary>No account with that key.</summary>
    Missing,

    /// <summary>Will be created by the backend at first sign-in.</summary>
    Pending,

    /// <summary>An account with that key exists but disagrees; never auto-merged.</summary>
    Conflict,

    /// <summary>The account exists and is disabled.</summary>
    Disabled,

    /// <summary>The backend could not be asked.</summary>
    Error,
}

/// <param name="Detail">Why the state is what it is, for the admin (never a secret).</param>
/// <param name="DownstreamId">The backend's own id for the account, when it has one.</param>
public sealed record BackendLink(BackendKind Backend, LinkState State, string? Detail = null, string? DownstreamId = null);

public enum PlannedAction
{
    Create,
    Link,
    Update,
    Skip,
    Conflict,
}

public sealed record PlannedStep(BackendKind Backend, PlannedAction Action, string Reason);

/// <summary>What applying a provisioning request would do, shown before anything is written.</summary>
public sealed record ProvisioningPlan(IReadOnlyList<PlannedStep> Steps)
{
    public bool HasConflict => Steps.Any(static s => s.Action == PlannedAction.Conflict);

    public PlannedStep For(BackendKind backend) => Steps.Single(s => s.Backend == backend);
}

public enum CredentialDelivery
{
    /// <summary>Nothing is sent (re-linking a user who already has a password).</summary>
    None,

    /// <summary>Keycloak e-mails a set-password link to the user's address.</summary>
    ActionEmail,

    /// <summary>A system-generated temporary password, shown once, changed at first sign-in.</summary>
    TemporaryPassword,
}

/// <summary>A generated one-time password. <see cref="ToString"/> never reveals it.</summary>
public sealed record TemporaryCredential(string Password)
{
    public override string ToString() => "TemporaryCredential { Password = *** }";
}

/// <summary>An expected DNS record for a mail domain.</summary>
public sealed record DnsRecord(string Name, string Type, int? Ttl, string Value);

/// <summary>A domain of an entity as the mail server sees it.</summary>
public sealed record DomainStatus(MailDomain Domain, BackendLink Mail, bool DkimPresent);

public enum DeletionTarget
{
    Mail,
    GroupwareData,
}

/// <summary>What a data deletion would remove and keep, shown on its confirmation page.</summary>
public sealed record DeletionPreview(
    ManagedUser User,
    DeletionTarget Target,
    bool AlreadyAbsent,
    long? MailboxBytes,
    IReadOnlyList<string> Removes,
    IReadOnlyList<string> Keeps);

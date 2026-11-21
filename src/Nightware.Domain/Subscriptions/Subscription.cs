using Nightware.Domain.Kernel;

namespace Nightware.Domain.Subscriptions;

/// <summary>What a subscription watches (ADR 0006 D10).</summary>
public enum SubscriptionResourceKind
{
    /// <summary>Messages in one folder (<see cref="SubscriptionTarget.Folder"/>), or in the whole mailbox when it is null.</summary>
    Messages,

    /// <summary>Events in the default calendar.</summary>
    Events,

    /// <summary>Contacts in the default folder.</summary>
    Contacts,

    /// <summary>Tasks in one To Do list.</summary>
    Tasks,
}

[Flags]
public enum SubscriptionChangeType
{
    None = 0,
    Created = 1,
    Updated = 2,
    Deleted = 4,
}

/// <summary>The parsed <c>resource</c>: whose data, of what kind, and which folder or list.</summary>
/// <param name="Folder">A mail folder id or well-known name, or a To Do list id; null for the whole collection.</param>
public sealed record SubscriptionTarget(MailboxRef Mailbox, SubscriptionResourceKind Kind, string? Folder);

/// <summary>A Graph change-notification subscription: the façade's one piece of data of record.</summary>
/// <param name="Resource">The <c>resource</c> as the client gave it, echoed back.</param>
/// <param name="CreatorId">The subscriber (<c>sub</c>); only they see or change it.</param>
public sealed record Subscription(
    string Id,
    string CreatorId,
    string Resource,
    SubscriptionTarget Target,
    SubscriptionChangeType ChangeType,
    Uri NotificationUrl,
    DateTimeOffset ExpirationDateTime)
{
    public string? ClientState { get; init; }

    public Uri? LifecycleNotificationUrl { get; init; }

    /// <summary>The client that created it (the token's <c>azp</c>).</summary>
    public string? ApplicationId { get; init; }

    /// <summary>The adapter's delta token for <see cref="Target"/>: what has already been notified.</summary>
    public string? SyncState { get; init; }
}

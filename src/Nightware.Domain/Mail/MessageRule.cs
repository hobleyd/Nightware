using Nightware.Domain.Kernel;

namespace Nightware.Domain.Mail;

/// <summary>
/// Graph <c>messageRulePredicates</c>, limited to what a Sieve script can test (ADR 0006 D5). Each list matches
/// when any of its values does; the predicates that are set must all match.
/// </summary>
public sealed record MessageRulePredicates
{
    public IReadOnlyList<string> SubjectContains { get; init; } = [];

    public IReadOnlyList<string> BodyContains { get; init; } = [];

    public IReadOnlyList<string> BodyOrSubjectContains { get; init; } = [];

    /// <summary>Text in the sender's name or address.</summary>
    public IReadOnlyList<string> SenderContains { get; init; } = [];

    public IReadOnlyList<string> RecipientContains { get; init; } = [];

    public IReadOnlyList<EmailAddress> FromAddresses { get; init; } = [];

    public IReadOnlyList<EmailAddress> SentToAddresses { get; init; } = [];

    public bool? HasAttachments { get; init; }

    public Importance? Importance { get; init; }

    public bool IsEmpty =>
        SubjectContains.Count == 0 && BodyContains.Count == 0 && BodyOrSubjectContains.Count == 0 && SenderContains.Count == 0
        && RecipientContains.Count == 0 && FromAddresses.Count == 0 && SentToAddresses.Count == 0 && HasAttachments is null && Importance is null;
}

/// <summary>Graph <c>messageRuleActions</c>, limited to what a Sieve script can do.</summary>
public sealed record MessageRuleActions
{
    public FolderId? MoveToFolder { get; init; }

    public FolderId? CopyToFolder { get; init; }

    /// <summary>To Deleted Items.</summary>
    public bool Delete { get; init; }

    /// <summary>Discarded on delivery; never stored.</summary>
    public bool PermanentDelete { get; init; }

    public bool MarkAsRead { get; init; }

    public IReadOnlyList<EmailAddress> ForwardTo { get; init; } = [];

    public IReadOnlyList<EmailAddress> RedirectTo { get; init; } = [];

    public bool StopProcessingRules { get; init; }

    public bool IsEmpty =>
        MoveToFolder is null && CopyToFolder is null && !Delete && !PermanentDelete && !MarkAsRead
        && ForwardTo.Count == 0 && RedirectTo.Count == 0 && !StopProcessingRules;
}

/// <summary>Graph <c>messageRule</c> on the Inbox. <see cref="Sequence"/> is 1-based and dense, as Exchange renumbers it.</summary>
public sealed record MessageRule(string Id, string DisplayName, int Sequence, bool IsEnabled)
{
    public MessageRulePredicates Conditions { get; init; } = new();

    public MessageRulePredicates Exceptions { get; init; } = new();

    public MessageRuleActions Actions { get; init; } = new();
}

namespace Nightware.Application.Common.Query;

public enum SearchField
{
    Any,
    From,
    To,
    Cc,
    Participants,
    Subject,
    Body,
    Attachment,
    HasAttachments,
    Received,
    Sent,
}

public enum SearchOp
{
    Contains,
    Equals,
    GreaterOrEqual,
    LessOrEqual,
    GreaterThan,
    LessThan,
}

/// <summary>One KQL term, e.g. <c>from:alice</c> or <c>received&gt;=2026-01-01</c>.</summary>
public sealed record SearchTerm(SearchField Field, SearchOp Op, string Value)
{
    public bool Negated { get; init; }
}

/// <summary>
/// Provider-neutral form of a <c>$search</c> expression. Terms are AND-ed; each
/// group in <see cref="AnyOf"/> is an OR of its terms.
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchTerm> Terms)
{
    public IReadOnlyList<IReadOnlyList<SearchTerm>> AnyOf { get; init; } = [];

    /// <summary>Plain words with no field prefix, joined by spaces.</summary>
    public string? FreeText => Terms.Where(t => t.Field == SearchField.Any && !t.Negated).Select(t => t.Value) is var words && words.Any()
        ? string.Join(' ', words)
        : null;
}

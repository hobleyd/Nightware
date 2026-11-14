namespace Nightware.Application.Common.Query;

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>One <c>$orderby</c> clause. <paramref name="Property"/> is the Graph property name.</summary>
public sealed record OrderBy(string Property, SortDirection Direction);

/// <summary>One <c>$expand</c> clause, e.g. <c>attachments($select=id,name)</c>.</summary>
public sealed record ExpandClause(string NavigationProperty, IReadOnlySet<string>? Select = null);

/// <summary>
/// Provider-neutral form of the OData query options a request carried. Built by
/// the Api from the whitelisted grammar in <c>docs/contract/odata-subset.md</c>;
/// adapters translate it, never re-parse Graph syntax.
/// </summary>
public sealed record ItemQuery
{
    public static ItemQuery Default { get; } = new();

    public FilterNode? Filter { get; init; }

    public IReadOnlyList<OrderBy> OrderBy { get; init; } = [];

    public int? Top { get; init; }

    public int? Skip { get; init; }

    /// <summary>Adapter-opaque continuation from a previous page's <c>$skiptoken</c>.</summary>
    public string? SkipToken { get; init; }

    /// <summary>Graph property names from <c>$select</c>; null means "default projection".</summary>
    public IReadOnlySet<string>? Select { get; init; }

    public SearchQuery? Search { get; init; }

    public IReadOnlyList<ExpandClause> Expand { get; init; } = [];

    public bool Selects(string property) => Select is null || Select.Contains(property);

    public ExpandClause? ExpandOf(string navigationProperty) =>
        Expand.FirstOrDefault(e => string.Equals(e.NavigationProperty, navigationProperty, StringComparison.OrdinalIgnoreCase));
}

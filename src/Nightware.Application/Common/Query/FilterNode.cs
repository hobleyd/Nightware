namespace Nightware.Application.Common.Query;

public enum CompareOp
{
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
}

/// <summary>A typed OData literal.</summary>
public abstract record FilterValue
{
    private FilterValue()
    {
    }

    public sealed record Text(string Value) : FilterValue;

    public sealed record Bool(bool Value) : FilterValue;

    public sealed record Number(decimal Value) : FilterValue;

    public sealed record Timestamp(DateTimeOffset Value) : FilterValue;

    /// <summary>An enum literal such as <c>microsoft.graph.followupFlagStatus'flagged'</c> or a bare name.</summary>
    public sealed record EnumMember(string Value) : FilterValue;

    public sealed record Null : FilterValue
    {
        public static Null Instance { get; } = new();
    }
}

/// <summary>A node of a parsed <c>$filter</c> expression. <c>Property</c> may be a path such as <c>flag/flagStatus</c>.</summary>
public abstract record FilterNode
{
    private FilterNode()
    {
    }

    public sealed record Compare(string Property, CompareOp Op, FilterValue Value) : FilterNode;

    public sealed record In(string Property, IReadOnlyList<FilterValue> Values) : FilterNode;

    public sealed record And(FilterNode Left, FilterNode Right) : FilterNode;

    public sealed record Or(FilterNode Left, FilterNode Right) : FilterNode;

    public sealed record Not(FilterNode Operand) : FilterNode;

    /// <summary>A function call such as <c>startswith(displayName,'a')</c>.</summary>
    public sealed record Call(string Function, string Property, FilterValue Argument) : FilterNode;

    /// <summary>A lambda such as <c>attendees/any(a: a/emailAddress/address eq 'x')</c>.</summary>
    public sealed record Any(string CollectionProperty, FilterNode Predicate) : FilterNode;

    /// <summary>Flattens nested <see cref="And"/> nodes into their conjuncts.</summary>
    public IEnumerable<FilterNode> Conjuncts()
    {
        if (this is And and)
        {
            foreach (var node in and.Left.Conjuncts())
            {
                yield return node;
            }

            foreach (var node in and.Right.Conjuncts())
            {
                yield return node;
            }
        }
        else
        {
            yield return this;
        }
    }
}

using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.OData;

/// <summary>
/// Hand-written recursive-descent parser for the <c>$filter</c> subset, producing <see cref="FilterNode"/>.
/// </summary>
/// <remarks>
/// <code>
/// expr       = and-expr *( "or" and-expr )
/// and-expr   = unary *( "and" unary )
/// unary      = "not" unary / primary
/// primary    = "(" expr ")" / function / lambda / comparison
/// function   = ( "startswith" / "contains" / "endswith" ) "(" path "," literal ")"
/// lambda     = path "/any(" var ":" expr ")"
/// comparison = path ( cmp-op literal / "in" "(" literal *( "," literal ) ")" )
/// path       = name *( "/" name )                  ; e.g. flag/flagStatus
/// literal    = string / number / datetime / true / false / null / qualified-name string
/// </code>
/// Inside a lambda, paths are made relative to the element: <c>a/emailAddress/address</c>
/// becomes <c>emailAddress/address</c>, and the bare variable becomes <see cref="LambdaElement"/>.
/// Anything else is <see cref="ErrorKind.Validation"/> naming the construct.
/// </remarks>
public static class FilterParser
{
    /// <summary>The property name used for the lambda element itself (<c>categories/any(c: c eq 'x')</c>).</summary>
    public const string LambdaElement = "$this";

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase) { "startswith", "contains", "endswith" };

    public static Result<FilterNode> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("$filter is empty.", ODataQueryParser.FilterOption);
        }

        var tokens = FilterLexer.Tokenize(text);
        if (!tokens.IsSuccess)
        {
            return tokens.Error;
        }

        var state = new State(tokens.Value);
        var node = ParseOr(state);
        if (!node.IsSuccess)
        {
            return node;
        }

        return state.Current.Kind == FilterTokenKind.End
            ? node
            : Unexpected(state.Current);
    }

    private static Result<FilterNode> ParseOr(State state)
    {
        var left = ParseAnd(state);
        while (left.IsSuccess && state.IsKeyword("or"))
        {
            state.Advance();
            var right = ParseAnd(state);
            left = right.IsSuccess ? new FilterNode.Or(left.Value, right.Value) : right;
        }

        return left;
    }

    private static Result<FilterNode> ParseAnd(State state)
    {
        var left = ParseUnary(state);
        while (left.IsSuccess && state.IsKeyword("and"))
        {
            state.Advance();
            var right = ParseUnary(state);
            left = right.IsSuccess ? new FilterNode.And(left.Value, right.Value) : right;
        }

        return left;
    }

    private static Result<FilterNode> ParseUnary(State state)
    {
        if (state.IsKeyword("not"))
        {
            state.Advance();
            return ParseUnary(state).Map(static operand => (FilterNode)new FilterNode.Not(operand));
        }

        return ParsePrimary(state);
    }

    private static Result<FilterNode> ParsePrimary(State state)
    {
        var token = state.Current;
        if (token.Kind == FilterTokenKind.OpenParen)
        {
            state.Advance();
            var inner = ParseOr(state);
            if (!inner.IsSuccess)
            {
                return inner;
            }

            return state.Expect(FilterTokenKind.CloseParen) is { } closeError ? closeError : inner;
        }

        if (token.Kind != FilterTokenKind.Identifier)
        {
            return Unexpected(token);
        }

        if (state.Peek(1).Kind == FilterTokenKind.OpenParen)
        {
            return ParseFunction(state);
        }

        return ParsePathExpression(state);
    }

    private static Result<FilterNode> ParseFunction(State state)
    {
        var name = state.Current;
        if (!Functions.Contains(name.Text))
        {
            return Error.Validation($"The $filter function '{name.Text}' is not supported.", ODataQueryParser.FilterOption);
        }

        state.Advance(); // name
        state.Advance(); // (
        var path = ParsePath(state, allowLambda: false);
        if (!path.IsSuccess)
        {
            return path.Error;
        }

        if (state.Expect(FilterTokenKind.Comma) is { } commaError)
        {
            return commaError;
        }

        var argument = ParseLiteral(state);
        if (!argument.IsSuccess)
        {
            return argument.Error;
        }

        if (state.Expect(FilterTokenKind.CloseParen) is { } closeError)
        {
            return closeError;
        }

        return new FilterNode.Call(name.Text.ToLowerInvariant(), path.Value.Property, argument.Value);
    }

    private static Result<FilterNode> ParsePathExpression(State state)
    {
        var path = ParsePath(state, allowLambda: true);
        if (!path.IsSuccess)
        {
            return path.Error;
        }

        if (path.Value.Lambda is { } lambda)
        {
            return lambda;
        }

        var property = path.Value.Property;
        var op = state.Current;
        if (op.Kind != FilterTokenKind.Identifier)
        {
            return Error.Validation($"Expected a comparison after '{property}' in $filter.", ODataQueryParser.FilterOption);
        }

        if (string.Equals(op.Text, "in", StringComparison.OrdinalIgnoreCase))
        {
            state.Advance();
            return ParseIn(state, property);
        }

        CompareOp? compare = op.Text.ToLowerInvariant() switch
        {
            "eq" => CompareOp.Eq,
            "ne" => CompareOp.Ne,
            "gt" => CompareOp.Gt,
            "ge" => CompareOp.Ge,
            "lt" => CompareOp.Lt,
            "le" => CompareOp.Le,
            _ => null,
        };
        if (compare is null)
        {
            return Error.Validation($"The $filter operator '{op.Text}' is not supported.", ODataQueryParser.FilterOption);
        }

        state.Advance();
        return ParseLiteral(state).Map(value => (FilterNode)new FilterNode.Compare(property, compare.Value, value));
    }

    private static Result<FilterNode> ParseIn(State state, string property)
    {
        if (state.Expect(FilterTokenKind.OpenParen) is { } openError)
        {
            return openError;
        }

        var values = new List<FilterValue>();
        while (true)
        {
            var value = ParseLiteral(state);
            if (!value.IsSuccess)
            {
                return value.Error;
            }

            values.Add(value.Value);
            if (state.Current.Kind == FilterTokenKind.Comma)
            {
                state.Advance();
                continue;
            }

            return state.Expect(FilterTokenKind.CloseParen) is { } closeError
                ? closeError
                : new FilterNode.In(property, values);
        }
    }

    private sealed record PathResult(string Property, FilterNode? Lambda);

    private static Result<PathResult> ParsePath(State state, bool allowLambda)
    {
        var segments = new List<string>();
        while (true)
        {
            var token = state.Current;
            if (token.Kind != FilterTokenKind.Identifier)
            {
                return Unexpected(token);
            }

            var isLambdaOperator = segments.Count > 0
                && state.Peek(1).Kind == FilterTokenKind.OpenParen
                && token.Text is "any" or "all";
            if (isLambdaOperator)
            {
                if (!allowLambda || token.Text == "all")
                {
                    return Error.Validation($"The $filter lambda operator '{token.Text}' is not supported here.", ODataQueryParser.FilterOption);
                }

                var collection = state.Relativise(segments);
                return ParseLambda(state, collection).Map(lambda => new PathResult(collection, lambda));
            }

            segments.Add(token.Text);
            state.Advance();
            if (state.Current.Kind != FilterTokenKind.Slash)
            {
                return new PathResult(state.Relativise(segments), null);
            }

            state.Advance();
        }
    }

    private static Result<FilterNode> ParseLambda(State state, string collection)
    {
        state.Advance(); // any
        state.Advance(); // (
        var variable = state.Current;
        if (variable.Kind != FilterTokenKind.Identifier || state.Peek(1).Kind != FilterTokenKind.Colon)
        {
            return Error.Validation($"'{collection}/any()' needs a lambda variable and predicate, e.g. any(a: a/name eq 'x').", ODataQueryParser.FilterOption);
        }

        state.Advance(); // variable
        state.Advance(); // :
        state.LambdaVariables.Push(variable.Text);
        var predicate = ParseOr(state);
        state.LambdaVariables.Pop();
        if (!predicate.IsSuccess)
        {
            return predicate;
        }

        return state.Expect(FilterTokenKind.CloseParen) is { } closeError
            ? closeError
            : new FilterNode.Any(collection, predicate.Value);
    }

    private static Result<FilterValue> ParseLiteral(State state)
    {
        var token = state.Current;
        Result<FilterValue> result;
        switch (token.Kind)
        {
            case FilterTokenKind.String:
                result = new FilterValue.Text(token.Text);
                break;
            case FilterTokenKind.Number when FilterLexer.TryParseNumber(token.Text, out var number):
                result = new FilterValue.Number(number);
                break;
            case FilterTokenKind.DateTime when FilterLexer.TryParseDateTime(token.Text, out var instant):
                result = new FilterValue.Timestamp(instant);
                break;
            case FilterTokenKind.Identifier when token.Text is "true" or "false":
                result = new FilterValue.Bool(token.Text == "true");
                break;
            case FilterTokenKind.Identifier when token.Text == "null":
                result = FilterValue.Null.Instance;
                break;
            case FilterTokenKind.Identifier when state.Peek(1) is { Kind: FilterTokenKind.String, AdjacentToPrevious: true } member:
                state.Advance();
                result = new FilterValue.EnumMember(member.Text);
                break;
            default:
                return Error.Validation($"Unsupported $filter literal '{token.Text}' at position {token.Position}.", ODataQueryParser.FilterOption);
        }

        state.Advance();
        return result;
    }

    private static Error Unexpected(FilterToken token) =>
        token.Kind == FilterTokenKind.End
            ? Error.Validation("Unexpected end of $filter.", ODataQueryParser.FilterOption)
            : Error.Validation($"Unsupported $filter syntax '{token.Text}' at position {token.Position}.", ODataQueryParser.FilterOption);

    private sealed class State(IReadOnlyList<FilterToken> tokens)
    {
        private int _index;

        public Stack<string> LambdaVariables { get; } = new();

        public FilterToken Current => tokens[_index];

        public FilterToken Peek(int offset) => tokens[Math.Min(_index + offset, tokens.Count - 1)];

        public void Advance()
        {
            if (_index < tokens.Count - 1)
            {
                _index++;
            }
        }

        public bool IsKeyword(string keyword) =>
            Current.Kind == FilterTokenKind.Identifier && string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

        public Error? Expect(FilterTokenKind kind)
        {
            if (Current.Kind != kind)
            {
                return Unexpected(Current);
            }

            Advance();
            return null;
        }

        /// <summary>Joins path segments, stripping a leading lambda variable.</summary>
        public string Relativise(List<string> segments)
        {
            if (LambdaVariables.Count > 0 && segments[0] == LambdaVariables.Peek())
            {
                return segments.Count == 1 ? LambdaElement : string.Join('/', segments.Skip(1));
            }

            return string.Join('/', segments);
        }
    }
}

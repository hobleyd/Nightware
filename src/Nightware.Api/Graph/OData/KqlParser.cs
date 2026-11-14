using System.Globalization;
using System.Text;

using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.OData;

/// <summary>
/// Parses a Graph <c>$search</c> value (KQL) into the provider-neutral <see cref="SearchQuery"/>.
/// </summary>
/// <remarks>
/// <para>NightMail sends <c>$search="&lt;whatever the user typed&gt;"</c> with the outer quotes
/// always present and inner quotes <b>not</b> escaped, so exactly the first and last character
/// are stripped when they are both <c>"</c> (docs/contract/odata-subset.md §6).</para>
/// <para>Supported: free-text words and <c>"quoted phrases"</c> (implicit AND), field terms
/// <c>from: to: cc: participants: subject: body: attachment: hasAttachments: received: sent:</c>
/// (field names case-insensitive; <c>:</c> = contains, <c>=</c> = equals; dates also take
/// <c>&gt;= &lt;= &gt; &lt;</c>), upper-case <c>AND</c>/<c>OR</c>/<c>NOT</c> and parentheses.</para>
/// <para>Because the text is the user's, the parser is lenient: an unknown <c>prop:value</c>
/// (<c>re:</c>, <c>10:30</c>, a pasted URL), a malformed date, a stray operator or an unbalanced
/// quote or parenthesis is read as free text rather than rejected. The boolean structure is
/// normalised to conjunctive form: single-term clauses become <see cref="SearchQuery.Terms"/>,
/// multi-term clauses become <see cref="SearchQuery.AnyOf"/> groups. Only an empty search or an
/// expression whose conjunctive form exceeds <see cref="MaxClauses"/> is a validation error.</para>
/// </remarks>
public static class KqlParser
{
    public const int MaxClauses = 64;

    private static readonly Dictionary<string, SearchField> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["from"] = SearchField.From,
        ["to"] = SearchField.To,
        ["cc"] = SearchField.Cc,
        ["participants"] = SearchField.Participants,
        ["subject"] = SearchField.Subject,
        ["body"] = SearchField.Body,
        ["attachment"] = SearchField.Attachment,
        ["attachments"] = SearchField.Attachment,
        ["hasattachment"] = SearchField.HasAttachments,
        ["hasattachments"] = SearchField.HasAttachments,
        ["received"] = SearchField.Received,
        ["sent"] = SearchField.Sent,
    };

    public static Result<SearchQuery> Parse(string? value)
    {
        var text = StripOuterQuotes(value ?? string.Empty);
        var tokens = Tokenize(text);
        var parser = new Parser(tokens);
        var expression = parser.ParseAll();
        if (expression is null)
        {
            return Error.Validation("$search is empty.", ODataQueryParser.SearchOption);
        }

        var clauses = ToConjunctiveForm(ToNegationNormalForm(expression, negate: false));
        if (clauses is null)
        {
            return Error.Validation($"$search is too complex: it expands to more than {MaxClauses} clauses.", ODataQueryParser.SearchOption);
        }

        var terms = new List<SearchTerm>();
        var groups = new List<IReadOnlyList<SearchTerm>>();
        foreach (var clause in clauses)
        {
            var distinct = clause.Distinct().ToList();
            if (distinct.Count == 1)
            {
                terms.Add(distinct[0]);
            }
            else
            {
                groups.Add(distinct);
            }
        }

        return new SearchQuery(terms) { AnyOf = groups };
    }

    internal static string StripOuterQuotes(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    // ---- tokens -----------------------------------------------------------

    private enum TokenKind
    {
        Term,
        And,
        Or,
        Not,
        Open,
        Close,
    }

    private sealed record Token(TokenKind Kind, string Raw, SearchTerm? Term = null);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new Token(TokenKind.Open, "("));
                i++;
            }
            else if (c == ')')
            {
                tokens.Add(new Token(TokenKind.Close, ")"));
                i++;
            }
            else if (c == '"')
            {
                var phrase = ReadQuoted(text, ref i);
                if (!string.IsNullOrWhiteSpace(phrase))
                {
                    tokens.Add(FreeText(phrase.Trim()));
                }
            }
            else
            {
                tokens.Add(ReadChunk(text, ref i));
            }
        }

        return tokens;
    }

    private static Token ReadChunk(string text, ref int i)
    {
        var raw = new StringBuilder();
        var quotedValue = false;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '(' and not ')')
        {
            if (text[i] == '"' && raw.Length > 0 && raw[^1] is ':' or '=' or '<' or '>')
            {
                raw.Append(ReadQuoted(text, ref i));
                quotedValue = true;
                break;
            }

            raw.Append(text[i]);
            i++;
        }

        var chunk = raw.ToString();
        if (!quotedValue)
        {
            switch (chunk)
            {
                case "AND":
                    return new Token(TokenKind.And, chunk);
                case "OR":
                    return new Token(TokenKind.Or, chunk);
                case "NOT":
                    return new Token(TokenKind.Not, chunk);
            }
        }

        return TryFieldTerm(chunk) is { } term ? new Token(TokenKind.Term, chunk, term) : FreeText(chunk);
    }

    private static string ReadQuoted(string text, ref int i)
    {
        i++; // opening quote
        var start = i;
        while (i < text.Length && text[i] != '"')
        {
            i++;
        }

        var content = text[start..i];
        if (i < text.Length)
        {
            i++; // closing quote; an unterminated phrase runs to the end
        }

        return content;
    }

    private static Token FreeText(string text) =>
        new(TokenKind.Term, text, new SearchTerm(SearchField.Any, SearchOp.Contains, text));

    private static SearchTerm? TryFieldTerm(string chunk)
    {
        var nameEnd = 0;
        while (nameEnd < chunk.Length && char.IsAsciiLetter(chunk[nameEnd]))
        {
            nameEnd++;
        }

        if (nameEnd == 0 || nameEnd == chunk.Length || !Fields.TryGetValue(chunk[..nameEnd], out var field))
        {
            return null;
        }

        var rest = chunk[nameEnd..];
        (SearchOp op, int length) = rest switch
        {
            _ when rest.StartsWith(">=", StringComparison.Ordinal) => (SearchOp.GreaterOrEqual, 2),
            _ when rest.StartsWith("<=", StringComparison.Ordinal) => (SearchOp.LessOrEqual, 2),
            _ when rest.StartsWith('>') => (SearchOp.GreaterThan, 1),
            _ when rest.StartsWith('<') => (SearchOp.LessThan, 1),
            _ when rest.StartsWith('=') => (SearchOp.Equals, 1),
            _ when rest.StartsWith(':') => (SearchOp.Contains, 1),
            _ => ((SearchOp)(-1), 0),
        };
        var value = length == 0 ? string.Empty : rest[length..].Trim();
        if (length == 0 || value.Length == 0)
        {
            return null;
        }

        return field switch
        {
            SearchField.HasAttachments when op is SearchOp.Contains or SearchOp.Equals => ParseBool(value) is { } flag
                ? new SearchTerm(field, SearchOp.Equals, flag ? "true" : "false")
                : null,
            SearchField.Received or SearchField.Sent => NormaliseDate(value) is { } date
                ? new SearchTerm(field, op == SearchOp.Contains ? SearchOp.Equals : op, date)
                : null,
            SearchField.HasAttachments => null,
            _ when op is SearchOp.Contains or SearchOp.Equals => new SearchTerm(field, op, value),
            _ => null,
        };
    }

    private static bool? ParseBool(string value) => value.ToLowerInvariant() switch
    {
        "true" or "yes" or "1" => true,
        "false" or "no" or "0" => false,
        _ => null,
    };

    /// <summary>A date as <c>yyyy-MM-dd</c>, or an instant as <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    private static string? NormaliseDate(string value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
                ? Json.GraphDateTimeFormat.Instant(instant)
                : null;
    }

    // ---- expression tree ----------------------------------------------------

    private abstract record Node;

    private sealed record TermNode(SearchTerm Term) : Node;

    private sealed record AndNode(Node Left, Node Right) : Node;

    private sealed record OrNode(Node Left, Node Right) : Node;

    private sealed record NotNode(Node Operand) : Node;

    /// <summary>
    /// <c>or = and *("OR" and); and = unary *(["AND"] unary); unary = "NOT" unary / "(" or ")" / term</c>,
    /// reading anything out of place as free text.
    /// </summary>
    private sealed class Parser(List<Token> tokens)
    {
        private int _index;

        public Node? ParseAll()
        {
            Node? result = null;
            while (_index < tokens.Count)
            {
                var next = ParseOr();
                if (next is not null)
                {
                    result = result is null ? next : new AndNode(result, next);
                }
                else if (_index < tokens.Count)
                {
                    _index++; // a stray ')' at the top level
                }
            }

            return result;
        }

        private Node? ParseOr()
        {
            var left = ParseAnd();
            while (left is not null && Is(TokenKind.Or) && HasOperandAfterOperator())
            {
                _index++;
                var right = ParseAnd();
                left = right is null ? left : new OrNode(left, right);
            }

            return left;
        }

        private Node? ParseAnd()
        {
            var left = ParseUnary();
            while (left is not null && _index < tokens.Count && !Is(TokenKind.Close) && !(Is(TokenKind.Or) && HasOperandAfterOperator()))
            {
                if (Is(TokenKind.And) && HasOperandAfterOperator())
                {
                    _index++;
                }

                var right = ParseUnary();
                if (right is null)
                {
                    break;
                }

                left = new AndNode(left, right);
            }

            return left;
        }

        private Node? ParseUnary()
        {
            if (_index >= tokens.Count)
            {
                return null;
            }

            var token = tokens[_index];
            switch (token.Kind)
            {
                case TokenKind.Not when HasOperandAfterOperator():
                    _index++;
                    var operand = ParseUnary();
                    return operand is null ? null : new NotNode(operand);
                case TokenKind.Open:
                    _index++;
                    var inner = ParseOr();
                    if (Is(TokenKind.Close))
                    {
                        _index++;
                    }

                    return inner;
                case TokenKind.Close:
                    return null;
                case TokenKind.Term:
                    _index++;
                    return new TermNode(token.Term!);
                default:
                    // An operator with nothing to apply to is just a word the user typed.
                    _index++;
                    return new TermNode(new SearchTerm(SearchField.Any, SearchOp.Contains, token.Raw));
            }
        }

        private bool Is(TokenKind kind) => _index < tokens.Count && tokens[_index].Kind == kind;

        private bool HasOperandAfterOperator() =>
            _index + 1 < tokens.Count && tokens[_index + 1].Kind is TokenKind.Term or TokenKind.Open or TokenKind.Not;
    }

    private static Node ToNegationNormalForm(Node node, bool negate) => node switch
    {
        TermNode term => negate ? new TermNode(term.Term with { Negated = !term.Term.Negated }) : term,
        NotNode not => ToNegationNormalForm(not.Operand, !negate),
        AndNode and => negate
            ? new OrNode(ToNegationNormalForm(and.Left, true), ToNegationNormalForm(and.Right, true))
            : new AndNode(ToNegationNormalForm(and.Left, false), ToNegationNormalForm(and.Right, false)),
        OrNode or => negate
            ? new AndNode(ToNegationNormalForm(or.Left, true), ToNegationNormalForm(or.Right, true))
            : new OrNode(ToNegationNormalForm(or.Left, false), ToNegationNormalForm(or.Right, false)),
        _ => throw new InvalidOperationException("Unknown node."),
    };

    /// <summary>Conjunctive normal form as clauses of OR-ed terms; null when it would exceed <see cref="MaxClauses"/>.</summary>
    private static List<List<SearchTerm>>? ToConjunctiveForm(Node node)
    {
        switch (node)
        {
            case TermNode term:
                return [[term.Term]];
            case AndNode and:
                var left = ToConjunctiveForm(and.Left);
                var right = ToConjunctiveForm(and.Right);
                if (left is null || right is null || left.Count + right.Count > MaxClauses)
                {
                    return null;
                }

                return [.. left, .. right];
            case OrNode or:
                var a = ToConjunctiveForm(or.Left);
                var b = ToConjunctiveForm(or.Right);
                if (a is null || b is null || a.Count * b.Count > MaxClauses)
                {
                    return null;
                }

                return [.. from x in a from y in b select x.Concat(y).ToList()];
            default:
                throw new InvalidOperationException("Expected negation normal form.");
        }
    }
}

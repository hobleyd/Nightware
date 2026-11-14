using System.Globalization;
using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.OData;

internal enum FilterTokenKind
{
    Identifier,
    String,
    Number,
    DateTime,
    OpenParen,
    CloseParen,
    Comma,
    Colon,
    Slash,
    End,
}

/// <param name="Text">Identifier name, unescaped string content, or the raw literal.</param>
/// <param name="AdjacentToPrevious">No whitespace separated this token from the previous one (enum literals).</param>
internal sealed record FilterToken(FilterTokenKind Kind, string Text, int Position, bool AdjacentToPrevious);

/// <summary>Tokenises a <c>$filter</c> expression.</summary>
internal static class FilterLexer
{
    public static Result<IReadOnlyList<FilterToken>> Tokenize(string text)
    {
        var tokens = new List<FilterToken>();
        var i = 0;
        var lastEnd = -1;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            var adjacent = start == lastEnd;
            FilterToken token;
            switch (c)
            {
                case '(':
                    token = new(FilterTokenKind.OpenParen, "(", start, adjacent);
                    i++;
                    break;
                case ')':
                    token = new(FilterTokenKind.CloseParen, ")", start, adjacent);
                    i++;
                    break;
                case ',':
                    token = new(FilterTokenKind.Comma, ",", start, adjacent);
                    i++;
                    break;
                case ':':
                    token = new(FilterTokenKind.Colon, ":", start, adjacent);
                    i++;
                    break;
                case '/':
                    token = new(FilterTokenKind.Slash, "/", start, adjacent);
                    i++;
                    break;
                case '\'':
                    var str = ReadString(text, ref i);
                    if (str is null)
                    {
                        return Error.Validation($"Unterminated string literal at position {start} in $filter.", ODataQueryParser.FilterOption);
                    }

                    token = new(FilterTokenKind.String, str, start, adjacent);
                    break;
                default:
                    if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                    {
                        var literal = ReadWhile(text, ref i, static ch => char.IsLetterOrDigit(ch) || ch is ':' or '.' or '-' or '+');
                        token = new(LooksLikeDateTime(literal) ? FilterTokenKind.DateTime : FilterTokenKind.Number, literal, start, adjacent);
                    }
                    else if (char.IsLetter(c) || c is '_' or '$' or '@')
                    {
                        var name = ReadWhile(text, ref i, static ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or '$' or '@');
                        token = new(FilterTokenKind.Identifier, name, start, adjacent);
                    }
                    else
                    {
                        return Error.Validation($"Unsupported character '{c}' at position {start} in $filter.", ODataQueryParser.FilterOption);
                    }

                    break;
            }

            tokens.Add(token);
            lastEnd = i;
        }

        tokens.Add(new FilterToken(FilterTokenKind.End, string.Empty, text.Length, false));
        return tokens;
    }

    public static bool TryParseNumber(string literal, out decimal value) =>
        decimal.TryParse(literal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value);

    public static bool TryParseDateTime(string literal, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    private static bool LooksLikeDateTime(string literal) =>
        literal.Length >= 10 && literal[4] == '-' && literal[7] == '-';

    private static string? ReadString(string text, ref int i)
    {
        var builder = new StringBuilder();
        i++; // opening quote
        while (i < text.Length)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                return builder.ToString();
            }

            builder.Append(text[i]);
            i++;
        }

        return null;
    }

    private static string ReadWhile(string text, ref int i, Func<char, bool> predicate)
    {
        var start = i;
        while (i < text.Length && predicate(text[i]))
        {
            i++;
        }

        return text[start..i];
    }
}

using System.Globalization;
using System.Text;

using Nightware.Domain.Administration;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// Reads Stalwart's <c>Domain.dnsZoneFile</c> into <see cref="DnsRecord"/>s. Stalwart documents the
/// property only as "current DNS zone data" (Text), so this reads RFC 1035 §5 master-file syntax:
/// comments, parenthesised continuation, quoted strings with <c>\"</c>/<c>\DDD</c> escapes,
/// <c>$ORIGIN</c>, <c>$TTL</c> (with <c>1h</c>-style units), a blank owner repeating the previous one,
/// <c>@</c>, relative names, and TTL/class in either order.
/// </summary>
/// <remarks>
/// Names (owners and the targets of MX, CNAME, NS, PTR, DNAME, SRV) come out fully qualified
/// without the trailing dot. A TXT/SPF value is its character-strings joined with nothing between
/// them — the value a DNS host's form expects (a long DKIM key arrives split into ≤255-byte
/// strings). Lines that cannot be read are skipped and counted, never fatal.
/// </remarks>
internal static class ZoneFileParser
{
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase) { "IN", "CH", "CS", "HS" };

    public static IReadOnlyList<DnsRecord> Parse(string zone, string origin) => Parse(zone, origin, out _);

    /// <param name="origin">The domain the zone belongs to; the initial <c>$ORIGIN</c>.</param>
    /// <param name="skippedLines">Logical lines that were not records or understood directives.</param>
    public static IReadOnlyList<DnsRecord> Parse(string zone, string origin, out int skippedLines)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(origin);

        string currentOrigin = origin.Trim().TrimEnd('.');
        int? defaultTtl = null;
        int? lastTtl = null;
        string? lastOwner = null;
        skippedLines = 0;
        var records = new List<DnsRecord>();

        foreach (LogicalLine line in Tokenise(zone))
        {
            IReadOnlyList<Token> tokens = line.Tokens;
            if (tokens.Count == 0)
            {
                continue;
            }

            if (!line.OwnerOmitted && !tokens[0].Quoted && tokens[0].Text.StartsWith('$'))
            {
                if (!ApplyDirective(tokens, ref currentOrigin, ref defaultTtl))
                {
                    skippedLines++;
                }

                continue;
            }

            int index = 0;
            string? owner = lastOwner;
            if (!line.OwnerOmitted)
            {
                owner = Qualify(tokens[0].Text, currentOrigin);
                index = 1;
            }

            int? ttl = null;
            for (int fields = 0; fields < 2 && index < tokens.Count && !tokens[index].Quoted; fields++)
            {
                if (Classes.Contains(tokens[index].Text))
                {
                    index++;
                }
                else if (TryParseTtl(tokens[index].Text, out int parsed))
                {
                    ttl = parsed;
                    index++;
                }
                else
                {
                    break;
                }
            }

            if (owner is null || index >= tokens.Count - 1 || tokens[index].Quoted || !IsTypeName(tokens[index].Text))
            {
                skippedLines++;
                continue;
            }

            string type = tokens[index].Text.ToUpperInvariant();
            Token[] data = [.. tokens.Skip(index + 1)];
            if (ttl is not null)
            {
                lastTtl = ttl;
            }

            lastOwner = owner;
            records.Add(new DnsRecord(owner, type, ttl ?? defaultTtl ?? lastTtl, Value(type, data, currentOrigin)));
        }

        return records;
    }

    private static bool ApplyDirective(IReadOnlyList<Token> tokens, ref string origin, ref int? defaultTtl)
    {
        string directive = tokens[0].Text.ToUpperInvariant();
        if (tokens.Count < 2)
        {
            return false;
        }

        switch (directive)
        {
            case "$ORIGIN":
                origin = Qualify(tokens[1].Text, origin);
                return true;
            case "$TTL" when TryParseTtl(tokens[1].Text, out int ttl):
                defaultTtl = ttl;
                return true;
            default:
                return false; // $INCLUDE (never followed) and anything unknown
        }
    }

    private static string Value(string type, Token[] data, string origin)
    {
        switch (type)
        {
            case "TXT" or "SPF":
                return string.Concat(data.Select(static t => t.Text));
            case "CNAME" or "NS" or "PTR" or "DNAME" when data.Length == 1:
                return Qualify(data[0].Text, origin);
            case "MX" when data.Length == 2:
                return $"{data[0].Text} {Qualify(data[1].Text, origin)}";
            case "SRV" when data.Length == 4:
                return $"{data[0].Text} {data[1].Text} {data[2].Text} {(data[3].Text == "." ? "." : Qualify(data[3].Text, origin))}";
            default:
                return string.Join(' ', data.Select(static t => t.Quoted ? Quote(t.Text) : t.Text));
        }
    }

    /// <summary>A name made absolute against <paramref name="origin"/>, without the trailing dot.</summary>
    private static string Qualify(string name, string origin)
    {
        if (name == "@")
        {
            return origin;
        }

        if (name.EndsWith('.'))
        {
            return name.TrimEnd('.');
        }

        return origin.Length == 0 ? name : $"{name}.{origin}";
    }

    private static string Quote(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool IsTypeName(string token) =>
        token.Length > 0 && char.IsAsciiLetter(token[0]) && token.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Seconds, or BIND units (<c>1w2d3h4m5s</c>, case-insensitive).</summary>
    internal static bool TryParseTtl(string token, out int seconds)
    {
        seconds = 0;
        if (token.Length == 0 || !char.IsAsciiDigit(token[0]))
        {
            return false;
        }

        if (token.All(char.IsAsciiDigit))
        {
            return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out seconds);
        }

        long total = 0;
        long number = 0;
        bool pendingNumber = false;
        foreach (char c in token)
        {
            if (char.IsAsciiDigit(c))
            {
                number = (number * 10) + (c - '0');
                pendingNumber = true;
                if (number > int.MaxValue)
                {
                    return false;
                }

                continue;
            }

            long unit = char.ToLowerInvariant(c) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                'd' => 86400,
                'w' => 604800,
                _ => 0,
            };
            if (unit == 0 || !pendingNumber)
            {
                return false;
            }

            total += number * unit;
            number = 0;
            pendingNumber = false;
        }

        total += number; // a trailing bare number counts as seconds
        if (total > int.MaxValue)
        {
            return false;
        }

        seconds = (int)total;
        return true;
    }

    private readonly record struct Token(string Text, bool Quoted);

    /// <param name="OwnerOmitted">The entry began with blank space, so it repeats the previous owner.</param>
    private sealed record LogicalLine(IReadOnlyList<Token> Tokens, bool OwnerOmitted);

    private static IEnumerable<LogicalLine> Tokenise(string zone)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        bool inQuote = false;
        bool hasToken = false;
        int depth = 0;
        bool atLineStart = true;
        bool ownerOmitted = false;

        void EndToken(bool quoted)
        {
            if (hasToken || quoted)
            {
                tokens.Add(new Token(current.ToString(), quoted));
            }

            current.Clear();
            hasToken = false;
        }

        for (int i = 0; i < zone.Length; i++)
        {
            char c = zone[i];
            if (atLineStart)
            {
                ownerOmitted = c is ' ' or '\t';
                atLineStart = false;
            }

            if (inQuote)
            {
                if (c == '"')
                {
                    inQuote = false;
                    EndToken(quoted: true);
                }
                else if (c == '\\' && i + 1 < zone.Length)
                {
                    current.Append(Unescape(zone, ref i));
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    EndToken(quoted: false);
                    inQuote = true;
                    break;
                case ';':
                    while (i + 1 < zone.Length && zone[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
                case '(':
                    EndToken(quoted: false);
                    depth++;
                    break;
                case ')':
                    EndToken(quoted: false);
                    depth = Math.Max(0, depth - 1);
                    break;
                case '\n':
                    EndToken(quoted: false);
                    if (depth == 0)
                    {
                        yield return new LogicalLine([.. tokens], ownerOmitted);
                        tokens.Clear();
                        atLineStart = true;
                    }

                    break;
                case ' ' or '\t' or '\r':
                    EndToken(quoted: false);
                    break;
                case '\\' when i + 1 < zone.Length:
                    current.Append(Unescape(zone, ref i));
                    hasToken = true;
                    break;
                default:
                    current.Append(c);
                    hasToken = true;
                    break;
            }
        }

        EndToken(quoted: inQuote);
        if (tokens.Count > 0)
        {
            yield return new LogicalLine([.. tokens], ownerOmitted);
        }
    }

    /// <summary><c>\X</c> is X; <c>\DDD</c> is the octet with that decimal value. <paramref name="i"/> is on the backslash.</summary>
    private static char Unescape(string zone, ref int i)
    {
        if (i + 3 < zone.Length && char.IsAsciiDigit(zone[i + 1]) && char.IsAsciiDigit(zone[i + 2]) && char.IsAsciiDigit(zone[i + 3]))
        {
            int value = int.Parse(zone.AsSpan(i + 1, 3), NumberStyles.None, CultureInfo.InvariantCulture);
            i += 3;
            return (char)Math.Min(value, 255);
        }

        i++;
        return zone[i];
    }
}
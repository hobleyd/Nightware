using System.Text;

namespace Nightware.Adapters.Dav.Contacts.VCard;

/// <summary>
/// Tolerant vCard 2.1/3.0/4.0 reader: CRLF or LF line ends, folding (RFC 6350 §3.2),
/// quoted-printable with soft line breaks and CHARSET (v2.1/v3 imports), property groups
/// (<c>item1.EMAIL</c>), quoted parameter values and bare v2.1 type parameters.
/// </summary>
public static class VCardReader
{
    public static IReadOnlyList<VCardDocument> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cards = new List<VCardDocument>();
        List<VCardProperty>? current = null;
        var version = "3.0";
        foreach (var line in LogicalLines(text))
        {
            var property = ParseLine(line);
            if (property is null)
            {
                continue;
            }

            if (property.Is("BEGIN") && string.Equals(property.Value.Trim(), "VCARD", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                version = "3.0";
            }
            else if (property.Is("END") && string.Equals(property.Value.Trim(), "VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                {
                    cards.Add(new VCardDocument(version, current));
                }

                current = null;
            }
            else if (current is not null && property.Is("VERSION"))
            {
                version = property.Value.Trim();
            }
            else
            {
                current?.Add(property);
            }
        }

        return cards;
    }

    /// <summary>Unfolds physical lines; joins quoted-printable soft breaks (<c>=</c> at end of line).</summary>
    internal static IEnumerable<string> LogicalLines(string text)
    {
        var physical = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        StringBuilder? current = null;
        foreach (var line in physical)
        {
            if (current is not null && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }

            if (current is not null && IsQuotedPrintable(current) && current.Length > 0 && current[^1] == '=')
            {
                current.Length--;
                current.Append(line);
                continue;
            }

            if (current is not null)
            {
                yield return current.ToString();
            }

            current = new StringBuilder(line);
        }

        if (current is { Length: > 0 })
        {
            yield return current.ToString();
        }
    }

    private static bool IsQuotedPrintable(StringBuilder line)
    {
        var text = line.ToString();
        var colon = FindValueColon(text);
        var head = colon < 0 ? text : text[..colon];
        return head.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    internal static VCardProperty? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var colon = FindValueColon(line);
        if (colon <= 0)
        {
            return null;
        }

        var head = line[..colon];
        var value = line[(colon + 1)..];
        var segments = SplitParameters(head);
        var name = segments[0].Trim();
        string? group = null;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            group = name[..dot];
            name = name[(dot + 1)..];
        }

        var parameters = new List<VCardParameter>();
        var quotedPrintable = false;
        var charset = "utf-8";
        foreach (var segment in segments.Skip(1))
        {
            var equals = segment.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                if (string.Equals(segment, "QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
                {
                    quotedPrintable = true;
                }
                else if (segment.Length > 0)
                {
                    parameters.Add(new VCardParameter("TYPE", [segment.Trim()]));
                }

                continue;
            }

            var parameterName = segment[..equals].Trim();
            var parameterValue = segment[(equals + 1)..].Trim();
            if (string.Equals(parameterName, "ENCODING", StringComparison.OrdinalIgnoreCase)
                && string.Equals(parameterValue, "QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
            {
                quotedPrintable = true;
                continue;
            }

            if (string.Equals(parameterName, "CHARSET", StringComparison.OrdinalIgnoreCase))
            {
                charset = parameterValue.Trim('"');
                continue;
            }

            parameters.Add(new VCardParameter(parameterName, SplitParameterValues(parameterValue)));
        }

        if (quotedPrintable)
        {
            value = DecodeQuotedPrintable(value, charset);
        }

        return new VCardProperty(group, name.ToUpperInvariant(), parameters, value);
    }

    /// <summary>Index of the colon separating name/parameters from the value (colons inside quotes do not count).</summary>
    private static int FindValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ':' when !quoted:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }

    private static List<string> SplitParameters(string head)
    {
        var parts = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i < head.Length; i++)
        {
            if (head[i] == '"')
            {
                quoted = !quoted;
            }
            else if (head[i] == ';' && !quoted)
            {
                parts.Add(head[start..i]);
                start = i + 1;
            }
        }

        parts.Add(head[start..]);
        return parts;
    }

    private static List<string> SplitParameterValues(string value)
    {
        var values = new List<string>();
        var quoted = false;
        var current = new StringBuilder();
        foreach (var c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values.Where(v => v.Length > 0).ToList();
    }

    internal static string DecodeQuotedPrintable(string value, string charset)
    {
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(encoding.GetBytes(value[i].ToString()));
            }
        }

        return encoding.GetString(bytes.ToArray());
    }
}

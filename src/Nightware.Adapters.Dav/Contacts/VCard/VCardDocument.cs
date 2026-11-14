using System.Text;

namespace Nightware.Adapters.Dav.Contacts.VCard;

/// <summary>One parameter (<c>TYPE=work,voice</c>). vCard 2.1 bare parameters (<c>TEL;WORK</c>) become <c>TYPE</c>.</summary>
public sealed record VCardParameter(string Name, IReadOnlyList<string> Values);

/// <summary>
/// One content line. <see cref="Value"/> is still vCard-escaped (<c>\,</c> <c>\;</c> <c>\n</c>);
/// quoted-printable has already been decoded (and its ENCODING/CHARSET parameters dropped).
/// </summary>
public sealed record VCardProperty(string? Group, string Name, IReadOnlyList<VCardParameter> Parameters, string Value)
{
    /// <summary>All TYPE values, lower-cased (<c>TYPE=WORK,VOICE</c> and <c>TYPE=work;TYPE=voice</c> alike).</summary>
    public IReadOnlySet<string> Types =>
        Parameters.Where(p => string.Equals(p.Name, "TYPE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Values)
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(v => v.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    public bool HasType(string type) => Types.Contains(type);

    /// <summary>The unescaped text value.</summary>
    public string Text => VCardText.Unescape(Value);

    /// <summary>Unescaped components of a structured value (N, ADR, ORG).</summary>
    public IReadOnlyList<string> Components => VCardText.SplitStructured(Value);

    public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A parsed vCard (one BEGIN:VCARD…END:VCARD block).</summary>
public sealed class VCardDocument
{
    public VCardDocument(string version, IEnumerable<VCardProperty> properties)
    {
        Version = version;
        Properties = properties.ToList();
    }

    public string Version { get; set; }

    /// <summary>Every property except BEGIN/END/VERSION, in order.</summary>
    public List<VCardProperty> Properties { get; }

    public bool IsVersion4 => Version.StartsWith('4');

    public VCardProperty? First(string name) => Properties.FirstOrDefault(p => p.Is(name));

    public IEnumerable<VCardProperty> All(string name) => Properties.Where(p => p.Is(name));

    public void RemoveAll(Func<VCardProperty, bool> predicate) => Properties.RemoveAll(p => predicate(p));
}

/// <summary>vCard text escaping (RFC 6350 §3.4) and structured-value splitting.</summary>
public static class VCardText
{
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            builder.Append(c switch
            {
                '\\' => @"\\",
                ',' => @"\,",
                ';' => @"\;",
                '\n' => @"\n",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Splits on unescaped <c>;</c> and unescapes each component.</summary>
    public static IReadOnlyList<string> SplitStructured(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == ';')
            {
                parts.Add(Unescape(value[start..i]));
                start = i + 1;
            }
        }

        parts.Add(Unescape(value[start..]));
        return parts;
    }

    public static string JoinStructured(IEnumerable<string?> components) =>
        string.Join(';', components.Select(c => Escape(c ?? string.Empty)));
}

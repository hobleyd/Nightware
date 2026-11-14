using System.Globalization;
using System.Text;

namespace Nightware.Adapters.Dav.Contacts.VCard;

/// <summary>Writes vCards with CRLF line ends, folded at 75 octets without splitting UTF-8 sequences.</summary>
public static class VCardWriter
{
    public const int MaxLineOctets = 75;

    public static string Write(VCardDocument card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var builder = new StringBuilder();
        AppendFolded(builder, "BEGIN:VCARD");
        AppendFolded(builder, "VERSION:" + card.Version);
        foreach (var property in card.Properties)
        {
            AppendFolded(builder, Format(property));
        }

        AppendFolded(builder, "END:VCARD");
        return builder.ToString();
    }

    internal static string Format(VCardProperty property)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(property.Group))
        {
            builder.Append(property.Group).Append('.');
        }

        builder.Append(property.Name);
        foreach (var parameter in property.Parameters)
        {
            builder.Append(';').Append(parameter.Name).Append('=');
            builder.Append(string.Join(',', parameter.Values.Select(QuoteIfNeeded)));
        }

        return builder.Append(':').Append(property.Value).ToString();
    }

    /// <summary>Appends one content line, folding at <see cref="MaxLineOctets"/> octets (continuations start with a space).</summary>
    internal static void AppendFolded(StringBuilder builder, string line)
    {
        var octets = 0;
        var limit = MaxLineOctets;
        var enumerator = StringInfo.GetTextElementEnumerator(line);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var size = Encoding.UTF8.GetByteCount(element);
            if (octets + size > limit)
            {
                builder.Append("\r\n ");
                octets = 1;
            }

            builder.Append(element);
            octets += size;
        }

        builder.Append("\r\n");
    }

    private static string QuoteIfNeeded(string value) =>
        value.IndexOfAny([':', ';', ',']) >= 0 ? $"\"{value.Replace("\"", string.Empty, StringComparison.Ordinal)}\"" : value;
}

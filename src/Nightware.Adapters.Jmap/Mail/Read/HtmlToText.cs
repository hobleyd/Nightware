using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// Renders an HTML body as plain text for <c>Prefer: outlook.body-content-type="text"</c> when a
/// message has no <c>text/plain</c> alternative. Deliberately simple: block elements become line
/// breaks, list items get a bullet, everything else is dropped and entities are decoded.
/// </summary>
internal static partial class HtmlToText
{
    public static string Convert(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = Invisible().Replace(html, string.Empty);
        text = LineBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n• ");
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');

        var lines = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (lines.Length > 0)
            {
                lines.Append(blank > 0 ? "\r\n\r\n" : "\r\n");
            }

            lines.Append(line);
            blank = 0;
        }

        return lines.ToString();
    }

    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Invisible();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    // Only block *ends* break the line, so adjacent blocks are one line apart rather than two.
    [GeneratedRegex(@"</(p|div|h[1-6]|tr|table|ul|ol|blockquote|pre)\s*>|<hr\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}

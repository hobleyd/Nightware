using System.Net;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Jmap.MailboxSettings;

/// <summary>
/// A plain-text rendering of an automatic-reply HTML body, so the reply carries a
/// <c>text/plain</c> part for senders whose clients prefer it. Block ends and <c>&lt;br&gt;</c>
/// become line breaks; other markup is dropped.
/// </summary>
internal static partial class HtmlToText
{
    public static string Convert(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var text = Hidden().Replace(html, string.Empty);
        text = Breaks().Replace(text, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()));
        return BlankRuns().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6]|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();
}

using System.Net;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>Plain text ↔ minimal HTML for DESCRIPTION / X-ALT-DESC and Graph <c>bodyPreview</c>.</summary>
internal static partial class HtmlText
{
    public const int PreviewLength = 255;

    public static string FromPlain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var encoded = WebUtility.HtmlEncode(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        return encoded.Replace("\n", "<br>", StringComparison.Ordinal);
    }

    public static string ToPlain(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var withoutHidden = HiddenBlocks().Replace(html, string.Empty);
        var withBreaks = LineBreakTags().Replace(withoutHidden, "\n");
        var text = WebUtility.HtmlDecode(Tags().Replace(withBreaks, string.Empty));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd());
        return MultipleBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    public static string Preview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var collapsed = Whitespace().Replace(text, " ").Trim();
        return collapsed.Length <= PreviewLength ? collapsed : collapsed[..PreviewLength];
    }

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<\s*(br\s*/?|/p|/div|/li|/tr|/h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

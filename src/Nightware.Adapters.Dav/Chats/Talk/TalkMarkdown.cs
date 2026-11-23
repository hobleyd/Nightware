using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Renders the Markdown subset Talk's clients render (ADR 0009 D4) as the html Graph returns: paragraphs and line
/// breaks, bold, italic, strikethrough, inline and fenced code, links (http, https and mailto only), lists, quotes and
/// headings. Text is html-encoded before any markup is added, so a message can't inject tags.
/// </summary>
internal static partial class TalkMarkdown
{
    // Private-use characters fence an already-rendered fragment while the remaining inline rules run.
    private const char Open = '';
    private const char Close = '';

    /// <param name="placeholder">The html for a <c>{key}</c> placeholder of Talk's message parameters, by key; null keeps the text.</param>
    public static string ToHtml(string markdown, Func<string, string?> placeholder)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(placeholder);
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var html = new StringBuilder();
        RenderBlocks(lines, html, placeholder);
        return html.ToString();
    }

    /// <summary>Plain text (Talk's <c>markdown: false</c>) as html: encoded, with line breaks.</summary>
    public static string PlainToHtml(string text, Func<string, string?> placeholder)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fragments = new List<string>();
        var body = Placeholders(Encode(text), placeholder, fragments);
        return Restore(body, fragments).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    public static string Encode(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Whether a link target may become an <c>href</c>: http, https and mailto only.</summary>
    public static bool IsSafeLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    private static void RenderBlocks(IReadOnlyList<string> lines, StringBuilder html, Func<string, string?> placeholder)
    {
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                html.Append("<p>").Append(string.Join("<br>", paragraph.Select(l => Inline(l, placeholder)))).Append("</p>");
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Count && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                html.Append("<pre><code>").Append(Encode(string.Join('\n', code))).Append("</code></pre>");
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quoted = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    var q = lines[i].TrimStart()[1..];
                    quoted.Add(q.StartsWith(' ') ? q[1..] : q);
                }

                i--;
                html.Append("<blockquote>");
                RenderBlocks(quoted, html, placeholder);
                html.Append("</blockquote>");
                continue;
            }

            if (ListItem(trimmed) is { } first)
            {
                FlushParagraph();
                var ordered = first.Ordered;
                html.Append(ordered ? "<ol>" : "<ul>");
                for (; i < lines.Count && ListItem(lines[i].TrimStart()) is { } item && item.Ordered == ordered; i++)
                {
                    html.Append("<li>").Append(Inline(item.Text, placeholder)).Append("</li>");
                }

                i--;
                html.Append(ordered ? "</ol>" : "</ul>");
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                var level = heading.Groups[1].Length.ToString(CultureInfo.InvariantCulture);
                html.Append("<h").Append(level).Append('>').Append(Inline(heading.Groups[2].Value, placeholder)).Append("</h").Append(level).Append('>');
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
    }

    private static (bool Ordered, string Text)? ListItem(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' or '+' && line[1] == ' ')
        {
            return (false, line[2..]);
        }

        var ordered = OrderedItem().Match(line);
        return ordered.Success ? (true, ordered.Groups[1].Value) : null;
    }

    private static string Inline(string text, Func<string, string?> placeholder)
    {
        var fragments = new List<string>();
        var html = Encode(text);
        html = CodeSpan().Replace(html, m => Fence(fragments, "<code>" + m.Groups[1].Value + "</code>"));
        html = Placeholders(html, placeholder, fragments);
        html = MarkdownLink().Replace(html, m =>
        {
            var url = Decode(m.Groups[2].Value);
            return IsSafeLink(url) ? Fence(fragments, $"<a href=\"{Encode(url)}\">{m.Groups[1].Value}</a>") : m.Value;
        });
        html = BareLink().Replace(html, m =>
        {
            var url = m.Value;
            var trail = url.Length - url.TrimEnd('.', ',', ';', ':', '!', '?', ')').Length;
            var target = url[..^trail];
            return IsSafeLink(Decode(target)) ? Fence(fragments, $"<a href=\"{target}\">{target}</a>") + url[^trail..] : url;
        });
        html = Bold().Replace(html, "<strong>$1$2</strong>");
        html = Italic().Replace(html, "<em>$1$2</em>");
        html = Strike().Replace(html, "<s>$1</s>");
        return Restore(html, fragments);
    }

    private static string Placeholders(string html, Func<string, string?> placeholder, List<string> fragments) =>
        Placeholder().Replace(html, m => placeholder(m.Groups[1].Value) is { } replacement ? Fence(fragments, replacement) : m.Value);

    private static string Fence(List<string> fragments, string html)
    {
        fragments.Add(html);
        return Open + (fragments.Count - 1).ToString(CultureInfo.InvariantCulture) + Close;
    }

    private static string Restore(string html, List<string> fragments)
    {
        // A link's text may hold a fenced code span or mention, so restore until none is left.
        for (var pass = 0; pass < 3 && html.Contains(Open, StringComparison.Ordinal); pass++)
        {
            html = Fenced().Replace(html, m => fragments[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }

        return html;
    }

    private static string Decode(string encoded) => encoded
        .Replace("&quot;", "\"", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal)
        .Replace("&gt;", ">", StringComparison.Ordinal)
        .Replace("&amp;", "&", StringComparison.Ordinal);

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\{([A-Za-z0-9_-]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?<![\w/""=-])(?:https?://|mailto:)[^\s<]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareLink();

    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__", RegexOptions.CultureInvariant)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])|(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])", RegexOptions.CultureInvariant)]
    private static partial Regex Italic();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.CultureInvariant)]
    private static partial Regex Strike();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\d{1,9}[.)]\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedItem();

    [GeneratedRegex("(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Fenced();
}

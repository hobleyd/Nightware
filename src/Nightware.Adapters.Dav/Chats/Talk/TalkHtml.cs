using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Converts a Graph html message body to the Markdown Talk keeps (ADR 0009 D4): emphasis, code, links, lists, quotes
/// and headings become their Markdown; <c>&lt;at id="n"&gt;</c> becomes the mention the caller supplies for it; any
/// other tag keeps its text. It is a tolerant scan, not a validating parser: Graph clients send small fragments.
/// </summary>
internal static partial class TalkHtml
{
    /// <param name="mention">The Markdown for <c>&lt;at id="n"&gt;</c> (e.g. <c>@"bob"</c>); null keeps the tag's text.</param>
    public static string ToMarkdown(string html, Func<int, string?> mention)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(mention);
        var output = new StringBuilder();
        var lists = new Stack<(bool Ordered, int Next)>();
        var quotes = new Stack<int>();
        var links = new Stack<string?>();
        var pre = 0;
        string? skipUntil = null;

        var position = 0;
        foreach (Match tag in Tag().Matches(html))
        {
            if (skipUntil is null)
            {
                Text(html[position..tag.Index]);
            }

            position = tag.Index + tag.Length;
            var closing = tag.Groups[1].Length > 0;
            var name = tag.Groups[2].Value.ToLowerInvariant();
            var attributes = tag.Groups[3].Value;
            if (skipUntil is not null)
            {
                if (closing && name == skipUntil)
                {
                    skipUntil = null;
                }

                continue;
            }

            switch (name)
            {
                case "b" or "strong":
                    output.Append("**");
                    break;
                case "i" or "em":
                    output.Append('*');
                    break;
                case "s" or "strike" or "del":
                    output.Append("~~");
                    break;
                case "code" when pre == 0:
                    output.Append('`');
                    break;
                case "pre":
                    if (closing)
                    {
                        pre = Math.Max(0, pre - 1);
                        Line();
                        output.Append("```");
                        Line();
                    }
                    else
                    {
                        Line();
                        output.Append("```\n");
                        pre++;
                    }

                    break;
                case "br":
                    output.Append('\n');
                    break;
                case "p" or "div":
                    Line();
                    break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Line();
                    if (!closing)
                    {
                        output.Append('#', name[1] - '0').Append(' ');
                    }

                    break;
                case "ul" or "ol":
                    Line();
                    if (closing)
                    {
                        if (lists.Count > 0)
                        {
                            lists.Pop();
                        }
                    }
                    else
                    {
                        lists.Push((name == "ol", 1));
                    }

                    break;
                case "li" when !closing:
                    Line();
                    if (lists.Count > 0 && lists.Peek().Ordered)
                    {
                        var (ordered, next) = lists.Pop();
                        lists.Push((ordered, next + 1));
                        output.Append(next.ToString(CultureInfo.InvariantCulture)).Append(". ");
                    }
                    else
                    {
                        output.Append("- ");
                    }

                    break;
                case "li":
                    Line();
                    break;
                case "blockquote":
                    if (closing)
                    {
                        if (quotes.Count > 0)
                        {
                            var start = quotes.Pop();
                            var quoted = output.ToString(start, output.Length - start).Trim('\n');
                            output.Length = start;
                            Line();
                            output.Append(string.Join('\n', quoted.Split('\n').Select(l => "> " + l)));
                            Line();
                        }
                    }
                    else
                    {
                        Line();
                        quotes.Push(output.Length);
                    }

                    break;
                case "a":
                    if (closing)
                    {
                        if (links.Count > 0 && links.Pop() is { } href)
                        {
                            output.Append("](").Append(href).Append(')');
                        }
                    }
                    else
                    {
                        var href = Attribute(attributes, "href");
                        var safe = href is not null && TalkMarkdown.IsSafeLink(href) ? href : null;
                        links.Push(safe);
                        if (safe is not null)
                        {
                            output.Append('[');
                        }
                    }

                    break;
                case "at" when !closing:
                    if (int.TryParse(Attribute(attributes, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && mention(id) is { } replacement)
                    {
                        output.Append(replacement);
                        skipUntil = "at";
                    }

                    break;
                case "emoji" or "img" when !closing:
                    // Teams sends emoji as <emoji alt="😀">; an image keeps its alternative text.
                    output.Append(WebUtility.HtmlDecode(Attribute(attributes, "alt") ?? string.Empty));
                    break;
                case "attachment" or "systemeventmessage" or "script" or "style" when !closing && !tag.Value.EndsWith("/>", StringComparison.Ordinal):
                    skipUntil = name;
                    break;
            }
        }

        if (skipUntil is null)
        {
            Text(html[position..]);
        }

        return Blank().Replace(output.ToString().Trim(), "\n\n");

        void Text(string raw)
        {
            if (raw.Length == 0)
            {
                return;
            }

            // Outside <pre>, html whitespace (source line breaks included) is a single space.
            var text = WebUtility.HtmlDecode(pre > 0 ? raw : Whitespace().Replace(raw, " "));
            if (pre == 0 && (output.Length == 0 || output[^1] == '\n'))
            {
                text = text.TrimStart();
            }

            output.Append(text);
        }

        void Line()
        {
            if (output.Length > 0 && output[^1] != '\n')
            {
                output.Append('\n');
            }
        }
    }

    private static string? Attribute(string attributes, string name)
    {
        foreach (Match attribute in AttributePattern().Matches(attributes))
        {
            if (string.Equals(attribute.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value
                    : attribute.Groups[4].Value);
            }
        }

        return null;
    }

    [GeneratedRegex(@"<(/?)([A-Za-z][A-Za-z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Blank();
}

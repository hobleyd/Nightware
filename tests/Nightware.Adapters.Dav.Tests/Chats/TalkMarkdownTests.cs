using Nightware.Adapters.Dav.Chats.Talk;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Talk's Markdown subset as Graph html (ADR 0009 D4).</summary>
public sealed class TalkMarkdownTests
{
    private static string Html(string markdown) => TalkMarkdown.ToHtml(markdown, key => key == "mention-user1" ? "<at id=\"0\">Bob Brown</at>" : null);

    [Theory]
    [InlineData("hello", "<p>hello</p>")]
    [InlineData("**bold** and __bold__", "<p><strong>bold</strong> and <strong>bold</strong></p>")]
    [InlineData("*it* and _it_ but snake_case_name and 2*3*4", "<p><em>it</em> and <em>it</em> but snake_case_name and 2*3*4</p>")]
    [InlineData("~~gone~~", "<p><s>gone</s></p>")]
    [InlineData("a `**code**` b", "<p>a <code>**code**</code> b</p>")]
    [InlineData("line one\nline two", "<p>line one<br>line two</p>")]
    [InlineData("para one\n\npara two", "<p>para one</p><p>para two</p>")]
    [InlineData("# Title", "<h1>Title</h1>")]
    [InlineData("- a\n- b", "<ul><li>a</li><li>b</li></ul>")]
    [InlineData("1. a\n2. b", "<ol><li>a</li><li>b</li></ol>")]
    [InlineData("> quoted\n> more", "<blockquote><p>quoted<br>more</p></blockquote>")]
    [InlineData("```\n<b>raw</b>\n```", "<pre><code>&lt;b&gt;raw&lt;/b&gt;</code></pre>")]
    public void Renders_the_subset_talks_clients_render(string markdown, string html) => Html(markdown).ShouldBe(html);

    [Fact]
    public void Text_is_encoded_before_markup_so_a_message_cannot_inject_tags()
    {
        Html("<script>alert(1)</script> & \"q\"").ShouldBe("<p>&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;q&quot;</p>");
    }

    [Fact]
    public void Links_are_rendered_for_http_https_and_mailto_only()
    {
        Html("[site](https://example.com/a?b=1&c=2)").ShouldBe("<p><a href=\"https://example.com/a?b=1&amp;c=2\">site</a></p>");
        Html("[x](javascript:alert(1))").ShouldBe("<p>[x](javascript:alert(1))</p>");
        Html("see https://example.com/x_y_z.").ShouldBe("<p>see <a href=\"https://example.com/x_y_z\">https://example.com/x_y_z</a>.</p>");
        Html("[mail](mailto:bob@example.com)").ShouldBe("<p><a href=\"mailto:bob@example.com\">mail</a></p>");
    }

    [Fact]
    public void A_message_cannot_forge_a_rendered_fragment()
    {
        Html("x \uE0007\uE001 **{mention-user1}** \uE00099999999999\uE001").ShouldBe("<p>x 7 <strong><at id=\"0\">Bob Brown</at></strong> 99999999999</p>");
        TalkMarkdown.PlainToHtml("\uE0000\uE001", _ => "<b>never</b>").ShouldBe("0");
    }

    [Fact]
    public void Placeholders_are_replaced_and_unknown_ones_kept()
    {
        Html("Hi {mention-user1} and {unknown}").ShouldBe("<p>Hi <at id=\"0\">Bob Brown</at> and {unknown}</p>");
    }

    [Fact]
    public void A_mention_inside_emphasis_is_kept_whole()
    {
        Html("**{mention-user1}**").ShouldBe("<p><strong><at id=\"0\">Bob Brown</at></strong></p>");
    }

    [Fact]
    public void Plain_text_is_encoded_with_line_breaks()
    {
        TalkMarkdown.PlainToHtml("a <b>\n{mention-user1}", key => key == "mention-user1" ? "<at id=\"0\">Bob</at>" : null)
            .ShouldBe("a &lt;b&gt;<br><at id=\"0\">Bob</at>");
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("/relative", false)]
    public void Only_safe_links_are_links(string url, bool safe) => TalkMarkdown.IsSafeLink(url).ShouldBe(safe);
}

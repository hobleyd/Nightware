using Nightware.Adapters.Dav.Chats.Talk;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Graph html bodies as the Markdown Talk keeps (ADR 0009 D4).</summary>
public sealed class TalkHtmlTests
{
    private static string Markdown(string html) => TalkHtml.ToMarkdown(html, id => id switch
    {
        0 => "@\"bob\"",
        1 => "@all",
        _ => null,
    });

    [Theory]
    [InlineData("<p>Hello</p>", "Hello")]
    [InlineData("<p>a <b>b</b> <strong>s</strong> <i>i</i> <em>e</em> <s>x</s> <code>c</code></p>", "a **b** **s** *i* *e* ~~x~~ `c`")]
    [InlineData("<p>one</p><p>two</p>", "one\ntwo")]
    [InlineData("one<br>two<br/>three", "one\ntwo\nthree")]
    [InlineData("<ul><li>a</li><li>b</li></ul>", "- a\n- b")]
    [InlineData("<ol><li>a</li><li>b</li></ol>", "1. a\n2. b")]
    [InlineData("<blockquote><p>q1</p><p>q2</p></blockquote><p>after</p>", "> q1\n> q2\nafter")]
    [InlineData("<h2>Title</h2>", "## Title")]
    [InlineData("<pre><code>x = 1\n  y</code></pre>", "```\nx = 1\n  y\n```")]
    [InlineData("<p>a &amp; b &lt;c&gt; &quot;d&quot;</p>", "a & b <c> \"d\"")]
    [InlineData("<div>source\n   whitespace</div>", "source whitespace")]
    [InlineData("<p>x <span style=\"color:red\">kept</span> <font>text</font></p>", "x kept text")]
    public void Converts_graph_html(string html, string markdown) => Markdown(html).ShouldBe(markdown);

    [Fact]
    public void Mentions_become_talks_and_an_unknown_one_keeps_its_text()
    {
        Markdown("<p>Hi <at id=\"0\">Bob Brown</at> and <at id=\"1\">Everyone</at>, <at id=\"7\">Nobody</at></p>")
            .ShouldBe("Hi @\"bob\" and @all, Nobody");
    }

    [Fact]
    public void Links_keep_safe_targets_only()
    {
        Markdown("<a href=\"https://example.com/?a=1&amp;b=2\">site</a>").ShouldBe("[site](https://example.com/?a=1&b=2)");
        Markdown("<a href=\"javascript:alert(1)\">bad</a>").ShouldBe("bad");
        Markdown("<a>bare</a>").ShouldBe("bare");
    }

    [Fact]
    public void Emoji_and_images_keep_their_alternative_text_and_attachments_and_scripts_go()
    {
        Markdown("<p>ok <emoji id=\"smile\" alt=\"😄\" title=\"Smile\"></emoji></p>").ShouldBe("ok 😄");
        Markdown("<p>see</p><attachment id=\"42\"></attachment><script>alert(1)</script>").ShouldBe("see");
    }
}

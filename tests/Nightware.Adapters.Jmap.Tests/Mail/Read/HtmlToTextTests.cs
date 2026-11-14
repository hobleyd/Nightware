using Nightware.Adapters.Jmap.Mail.Read;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class HtmlToTextTests
{
    [Fact]
    public void Blocks_become_lines_and_markup_is_dropped()
    {
        HtmlToText.Convert("<html><head><title>t</title><style>p{}</style></head><body><p>Hi <b>Alice</b>,</p><div>line&nbsp;two<br>three</div></body></html>")
            .ShouldBe("Hi Alice,\r\nline two\r\nthree");
    }

    [Fact]
    public void Lists_get_bullets_and_entities_are_decoded()
    {
        HtmlToText.Convert("<ul><li>a &amp; b</li><li>c&lt;d</li></ul>").ShouldBe("• a & b\r\n• c<d");
    }

    [Fact]
    public void Blank_lines_collapse_to_one_paragraph_break_and_scripts_vanish()
    {
        HtmlToText.Convert("one<br><br><br>  two  <script>alert(1)</script><!-- note -->").ShouldBe("one\r\n\r\ntwo");
    }

    [Fact]
    public void Empty_html_is_empty_text()
    {
        HtmlToText.Convert(string.Empty).ShouldBeEmpty();
    }
}

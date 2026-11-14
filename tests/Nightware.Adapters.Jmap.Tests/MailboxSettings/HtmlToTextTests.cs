using Nightware.Adapters.Jmap.MailboxSettings;

namespace Nightware.Adapters.Jmap.Tests.MailboxSettings;

public sealed class HtmlToTextTests
{
    [Theory]
    [InlineData("<div>Out of the office.</div>", "Out of the office.")]
    [InlineData("<p>Away</p><p>Back Monday</p>", "Away\nBack Monday")]
    [InlineData("Line one<br>Line two<br/>three", "Line one\nLine two\nthree")]
    [InlineData("<b>Bold</b> &amp; <i>more</i>&nbsp;here", "Bold & more here")]
    [InlineData("<style>p{color:red}</style><p>Shown</p><script>alert(1)</script>", "Shown")]
    [InlineData("<p>a</p><p></p><p></p><p></p><p>b</p>", "a\n\nb")]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    public void Converts(string html, string text) => HtmlToText.Convert(html).ShouldBe(text);
}

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Graph;

public sealed class PreferHeaderTests
{
    [Fact]
    public void Parses_the_utc_timezone_the_client_sends()
    {
        var prefer = PreferHeader.Parse(["outlook.timezone=\"UTC\""]);

        prefer.TimeZoneName.ShouldBe("UTC");
        prefer.TimeZone.ShouldBe(TimeZoneInfo.Utc);
        prefer.TimeZoneLabel.ShouldBe("UTC");
    }

    [Fact]
    public void Parses_the_plain_text_body_probe()
    {
        PreferHeader.Parse(["outlook.body-content-type=\"text\""]).BodyContentType.ShouldBe(BodyType.Text);
        PreferHeader.Parse(["outlook.body-content-type=\"HTML\""]).BodyContentType.ShouldBe(BodyType.Html);
    }

    [Theory]
    [InlineData("Australia/Sydney")]
    [InlineData("AUS Eastern Standard Time")]
    public void Accepts_iana_and_windows_zones(string zone)
    {
        var prefer = PreferHeader.Parse([$"outlook.timezone=\"{zone}\""]);

        prefer.TimeZoneName.ShouldBe(zone);
        prefer.TimeZone.ShouldNotBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Parses_several_preferences_across_and_within_headers()
    {
        var prefer = PreferHeader.Parse(["odata.maxpagesize=50, return=minimal", "outlook.timezone=\"UTC\"; ignored=1"]);

        prefer.MaxPageSize.ShouldBe(50);
        prefer.ReturnMinimal.ShouldBeTrue();
        prefer.TimeZoneName.ShouldBe("UTC");
    }

    [Fact]
    public void Ignores_unknown_and_malformed_preferences()
    {
        var prefer = PreferHeader.Parse(["outlook.timezone=\"Mars/Olympus\"", "odata.maxpagesize=-3", "odata.maxpagesize=x", "respond-async", "outlook.body-content-type=\"rtf\"", "return=representation", "", null]);

        prefer.ShouldBe(PreferHeader.None);
        prefer.TimeZone.ShouldBe(TimeZoneInfo.Utc);
        prefer.TimeZoneLabel.ShouldBe("UTC");
    }

    [Fact]
    public void Quoted_commas_do_not_split_preferences()
    {
        PreferHeader.Parse(["x=\"a,b\", odata.maxpagesize=7"]).MaxPageSize.ShouldBe(7);
    }

    [Fact]
    public async Task Binds_from_the_request()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[PreferHeader.HeaderName] = "outlook.timezone=\"UTC\"";

        var prefer = await PreferHeader.BindAsync(context, null!);

        prefer!.TimeZoneName.ShouldBe("UTC");
    }

    [Fact]
    public void Marks_preferences_applied()
    {
        var context = new DefaultHttpContext();
        var prefer = PreferHeader.Parse(["outlook.timezone=\"UTC\", outlook.body-content-type=\"text\""]);

        prefer.MarkTimeZoneApplied(context.Response);
        prefer.MarkBodyContentTypeApplied(context.Response);
        PreferHeader.MarkApplied(context.Response, "return", null);

        context.Response.Headers[PreferHeader.AppliedHeaderName].ToString()
            .ShouldBe("outlook.timezone=\"UTC\", outlook.body-content-type=\"text\", return");
    }

    [Fact]
    public void Marks_nothing_when_nothing_was_asked()
    {
        var context = new DefaultHttpContext();

        PreferHeader.None.MarkTimeZoneApplied(context.Response);
        PreferHeader.None.MarkBodyContentTypeApplied(context.Response);

        context.Response.Headers.ContainsKey(PreferHeader.AppliedHeaderName).ShouldBeFalse();
    }
}

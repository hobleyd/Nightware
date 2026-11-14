using System.Xml;
using Nightware.Adapters.Dav.Core;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavMultistatusTests
{
    [Fact]
    public void Parses_per_propstat_statuses_including_partial_404()
    {
        var multistatus = DavXml.ParseMultistatus(Fixture.Read("propfind-calendar-home.xml"));

        var home = multistatus.For("/remote.php/dav/calendars/alice")!;
        home.HasResourceType(DavNames.Collection).ShouldBeTrue();
        home.Find(DavNames.DisplayName).ShouldBeNull();
        home.StatusOf(DavNames.DisplayName).ShouldBe(404);
        home.StatusOf(DavNames.ResourceType).ShouldBe(200);
        home.StatusOf(DavNames.GetETag).ShouldBeNull();

        var personal = multistatus.For("/remote.php/dav/calendars/alice/personal/")!;
        personal.Text(DavNames.DisplayName).ShouldBe("Personal");
        personal.Text(DavNames.CalendarColor).ShouldBe("#0082c9");
        personal.StatusOf(DavNames.OwnCloudReadOnly).ShouldBe(404);
        personal.Text(DavNames.OwnCloudReadOnly).ShouldBeNull();
        personal.Text(DavNames.OwnCloudInvite).ShouldBeNull();
        personal.IsFound.ShouldBeTrue();
    }

    [Fact]
    public void Parses_response_level_status_sync_token_and_data()
    {
        var multistatus = DavXml.ParseMultistatus(Fixture.Read("multiget-partial.xml"));

        multistatus.Responses.Count.ShouldBe(2);
        var found = multistatus.Responses[0];
        found.ETag.ShouldBe("\"7b3d0a5c1e\"");
        found.CalendarData.ShouldNotBeNull().ShouldContain("BEGIN:VEVENT");
        var missing = multistatus.Responses[1];
        missing.IsFound.ShouldBeFalse();
        missing.StatusCode.ShouldBe(404);
        missing.Href.ShouldBe("/remote.php/dav/calendars/alice/personal/gone%20event.ics");
        missing.PropStats.ShouldBeEmpty();
    }

    [Fact]
    public void HrefsOf_reads_nested_hrefs()
    {
        var multistatus = DavXml.ParseMultistatus(Fixture.Read("propfind-principal-alice.xml"));
        var principal = multistatus.Responses.Single();
        principal.HrefOf(DavNames.CalendarHomeSet).ShouldBe("/remote.php/dav/calendars/alice/");
        principal.HrefsOf(DavNames.CalendarUserAddressSet).ShouldBe(["mailto:alice@example.com", "/remote.php/dav/principals/users/alice/"]);
        principal.HrefOf(DavNames.ScheduleOutboxUrl).ShouldBe("/remote.php/dav/calendars/alice/outbox/");
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK", 200)]
    [InlineData("HTTP/1.1 404 Not Found", 404)]
    [InlineData(" HTTP/1.1 507 Insufficient Storage ", 507)]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Status_lines(string? line, int? expected) => DavXml.ParseStatusLine(line).ShouldBe(expected);

    [Fact]
    public void Rejects_non_multistatus_and_dtds()
    {
        Should.Throw<FormatException>(() => DavXml.ParseMultistatus("<d:error xmlns:d=\"DAV:\"/>"));
        Should.Throw<XmlException>(() => DavXml.ParseMultistatus("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><d:multistatus xmlns:d=\"DAV:\">&e;</d:multistatus>"));
    }

    [Fact]
    public void Error_conditions_are_read_from_dav_error_bodies()
    {
        DavXml.ParseErrorConditions("<?xml version=\"1.0\"?><d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\"><d:valid-sync-token/><s:message>Invalid sync token</s:message></d:error>")
            .ShouldContain(DavNames.ValidSyncToken);
        DavXml.ParseErrorConditions("not xml").ShouldBeEmpty();
        DavXml.ParseErrorConditions("<broken").ShouldBeEmpty();
        DavXml.ParseErrorConditions(null).ShouldBeEmpty();
    }
}

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Core;

/// <summary>Cross-checks against exchanges recorded from Nextcloud 35.0.3 (deploy/recordings/caldav).</summary>
public sealed class RecordedNextcloudTests : IDisposable
{
    private readonly DavTestServer _dav = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Recorded(string name) => Fixture.Read(Path.Combine("Recorded", name));

    private void Serve(string method, string path, string recording, int status = 207, string contentType = "application/xml; charset=utf-8") =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(Recorded(recording)));

    [Fact]
    public async Task Principal_discovery_against_the_recorded_principal()
    {
        _dav.Server.Given(Request.Create().WithPath("/remote.php/dav/").UsingMethod("PROPFIND"))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(Recorded("01-principal.response.xml")));
        Serve("PROPFIND", "/remote.php/dav/principals/users/alice/", "01-principal.response.xml");
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var alice = (await new DavPrincipalDiscovery(_dav.Client, _dav.Credentials, cache, _dav.Paths, Options.Create(_dav.Options))
            .DiscoverAsync(new MailboxRef("alice@nightware.localhost"), Ct)).Value;

        alice.IsCurrentUser.ShouldBeTrue();
        alice.DisplayName.ShouldBe("Alice Anderson");
        alice.CalendarHomeSet.ShouldBe("/remote.php/dav/calendars/alice/");
        alice.AddressBookHomeSet.ShouldBe("/remote.php/dav/addressbooks/users/alice/");
        alice.ScheduleOutboxUrl.ShouldBe("/remote.php/dav/calendars/alice/outbox/");
        alice.ScheduleInboxUrl.ShouldBe("/remote.php/dav/calendars/alice/inbox/");
    }

    [Fact]
    public async Task Calendar_home_lists_calendars_but_not_inbox_outbox_or_trash()
    {
        Serve("PROPFIND", "/remote.php/dav/calendars/alice/", "02-calendar-home.response.xml");
        var alice = new DavPrincipal("/remote.php/dav/principals/users/alice/", true) { CalendarHomeSet = "/remote.php/dav/calendars/alice/" };

        var calendars = (await new DavCollectionCatalog(_dav.Client).ListCalendarsAsync(alice, Ct)).Value;

        calendars.Select(c => c.Href).ShouldBe(["/remote.php/dav/calendars/alice/personal/", "/remote.php/dav/calendars/alice/contact_birthdays/"]);
        var personal = calendars[0];
        personal.DisplayName.ShouldBe("Personal");
        personal.Color.ShouldBe("#00679e");
        personal.SyncToken.ShouldBe("http://sabre.io/ns/sync/10");
        personal.SupportsEvents.ShouldBeTrue();
        personal.IsShared.ShouldBeFalse();
        personal.IsReadOnly.ShouldBeFalse();
        calendars[1].IsShared.ShouldBeFalse();
    }

    [Fact]
    public async Task Time_range_query_parses_every_returned_event()
    {
        Serve("REPORT", "/remote.php/dav/calendars/alice/personal/", "03-calendar-query-timerange.response.xml");
        var query = new CalendarQuery("VEVENT") { TimeRange = new DavTimeRange(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero)) };

        var result = await _dav.Client.ReportAsync("calendars/alice/personal/", DavDepth.One, DavRequestBodies.CalendarQuery(query), Ct);

        var codec = new IcalNetCalendarCodec(_dav.Time);
        result.Value.Responses.Count.ShouldBe(4);
        foreach (var response in result.Value.Responses)
        {
            var href = _dav.Paths.ToRelative(response.Href)!;
            var document = codec.Parse(response.CalendarData!, new CalendarParseOptions { Href = href, Mailbox = new MailboxRef("alice@nightware.localhost") });
            var ev = document.Value.Events.ShouldHaveSingleItem();
            ev.Id.ToDavItemId().Value.Href.ShouldBe(href);
            ev.IsOrganizer.ShouldBeTrue();
            ev.ResponseStatus.Response.ShouldBe(ResponseType.Organizer);
            ev.Attendees.ShouldContain(a => a.Type == AttendeeType.Resource && a.EmailAddress.Address == "boardroom@nightware.localhost");
        }
    }

    [Fact]
    public async Task Initial_sync_collection_lists_members_and_the_token()
    {
        Serve("REPORT", "/remote.php/dav/calendars/alice/personal/", "04-sync-collection-initial.response.xml");
        var sync = (await _dav.Client.SyncCollectionAsync("calendars/alice/personal/", null, [DavNames.GetETag], null, Ct)).Value;
        sync.Changed.Count.ShouldBe(5);
        sync.Removed.ShouldBeEmpty();
        sync.IsTruncated.ShouldBeFalse();
        sync.SyncToken.ShouldBe("http://sabre.io/ns/sync/10");
        sync.Changed.ShouldAllBe(r => r.ETag != null);
    }

    [Fact]
    public async Task Multiget_returns_data_with_a_declined_room()
    {
        Serve("REPORT", "/remote.php/dav/calendars/alice/personal/", "05-calendar-multiget.response.xml");
        var response = (await _dav.Client.ReportAsync("calendars/alice/personal/", DavDepth.One, DavRequestBodies.CalendarMultiget(["/remote.php/dav/calendars/alice/personal/nightware-imip-1794691557-32407.ics"]), Ct)).Value.Responses.Single();
        var ev = new IcalNetCalendarCodec(_dav.Time).Parse(response.CalendarData!).Value.Events.Single();
        ev.Attendees.Single(a => a.Type == AttendeeType.Resource).Status.Response.ShouldBe(ResponseType.Declined);
        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 15, 10, 0, 0), "UTC"));
    }

    [Fact]
    public void Room_principals_expose_nextcloud_room_metadata()
    {
        var rooms = DavXml.ParseMultistatus(Recorded("06-room-principals.response.xml")).Responses.Where(r => r.Text(DavNames.RoomType) is not null).ToList();
        rooms.Count.ShouldBe(2);
        rooms[0].Text(DavNames.RoomSeatingCapacity).ShouldBe("12");
        rooms[0].Text(DavNames.RoomFeatures).ShouldNotBeNull().ShouldContain("WHEELCHAIR-ACCESSIBLE");
        rooms[0].Text(DavNames.RoomBuildingAddress).ShouldBe("1 Example Street, Sydney NSW 2000");
    }

    [Fact]
    public void Room_search_result_maps_to_a_room_principal()
    {
        var response = DavXml.ParseMultistatus(Recorded("07-principal-search-rooms.response.xml")).Responses.Single();
        var room = DavPrincipalDiscovery.ToPrincipal(response, isCurrentUser: false);
        room.IsRoomOrResource.ShouldBeTrue();
        room.HasAddress("BOARDROOM@nightware.localhost").ShouldBeTrue();
        room.DisplayName.ShouldBe("Boardroom");
    }

    [Fact]
    public void Outbox_is_a_schedule_outbox()
    {
        var outbox = DavXml.ParseMultistatus(Recorded("08-freebusy-outbox-props.response.xml")).Responses.Single();
        outbox.HasResourceType(DavNames.ScheduleOutbox).ShouldBeTrue();
    }

    [Fact]
    public async Task Free_busy_post_round_trip()
    {
        Serve("POST", "/remote.php/dav/calendars/alice/outbox/", "09-freebusy-post.response.xml", 200);
        var codec = new FreeBusyCodec(_dav.Time);
        var request = codec.BuildRequest("alice@nightware.localhost", ["boardroom@nightware.localhost", "bob@nightware.localhost"], new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), "fb-1");

        var items = (await _dav.Client.PostToOutboxAsync("/remote.php/dav/calendars/alice/outbox/", request, Ct)).Value;

        items.Select(i => i.Recipient).ShouldBe(["mailto:boardroom@nightware.localhost", "mailto:bob@nightware.localhost"]);
        items.ShouldAllBe(i => i.IsSuccess);
        var busy = FreeBusyCodec.ParseReply(items[0].CalendarData).ShouldHaveSingleItem();
        busy.Start.ShouldBe(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        busy.End.ShouldBe(new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero));
        busy.Status.ShouldBe(FreeBusyStatus.Busy);
        _dav.Server.LogEntries.Single().RequestMessage!.Body!.ShouldContain("ATTENDEE:mailto:bob@nightware.localhost");
    }

    public void Dispose() => _dav.Dispose();
}

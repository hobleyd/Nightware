using System.Net;
using System.Text;
using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavClientTests : IDisposable
{
    private const string Personal = "/remote.php/dav/calendars/alice/personal/";
    private const string Event = Personal + "standup.ics";
    private readonly DavTestServer _dav = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WireMockServer Server => _dav.Server;

    private void Respond(string method, string path, int status, string? body = null, string contentType = "application/xml; charset=utf-8", params (string Name, string Value)[] headers)
    {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null)
        {
            response = response.WithHeader("Content-Type", contentType).WithBody(body);
        }

        foreach (var (name, value) in headers)
        {
            response = response.WithHeader(name, value);
        }

        Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(response);
    }

    private WireMock.Logging.ILogEntry LastRequest => Server.LogEntries[^1];

    private XDocument LastBody => XDocument.Parse(LastRequest.RequestMessage!.Body!);

    private string? Header(string name) =>
        LastRequest.RequestMessage!.Headers!.TryGetValue(name, out var values) ? values[0] : null;

    [Fact]
    public async Task PropFind_sends_depth_props_and_bearer_and_parses_the_multistatus()
    {
        Respond("PROPFIND", "/remote.php/dav/calendars/alice/", 207, Fixture.Read("propfind-calendar-home.xml"));

        var result = await _dav.Client.PropFindAsync("calendars/alice/", DavDepth.One, [DavNames.DisplayName, DavNames.ResourceType], Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Responses.Count.ShouldBe(7);
        Header("Depth").ShouldBe("1");
        Header("Authorization").ShouldBe("Bearer " + DavTestServer.Token);
        Header("Content-Type").ShouldStartWith("application/xml");
        LastBody.Root!.Name.ShouldBe(DavNs.Dav + "propfind");
        LastBody.Descendants(DavNames.DisplayName).ShouldHaveSingleItem();
        LastBody.Descendants(DavNames.ResourceType).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PropFind_depth_zero()
    {
        Respond("PROPFIND", "/remote.php/dav/principals/users/alice/", 207, Fixture.Read("propfind-principal-alice.xml"));
        var result = await _dav.Client.PropFindAsync("/remote.php/dav/principals/users/alice/", DavDepth.Zero, [DavNames.CalendarHomeSet], Ct);
        result.IsSuccess.ShouldBeTrue();
        Header("Depth").ShouldBe("0");
    }

    [Fact]
    public async Task Report_calendar_query_with_time_range_and_expand()
    {
        Respond("REPORT", Personal, 207, Fixture.Read("calendar-query-expanded.xml"));
        var query = new CalendarQuery("VEVENT")
        {
            TimeRange = new DavTimeRange(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero)),
            Expand = new DavTimeRange(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero)),
        };

        var result = await _dav.Client.ReportAsync(Personal, DavDepth.One, DavRequestBodies.CalendarQuery(query), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Responses.ShouldAllBe(r => r.CalendarData != null);
        Header("Depth").ShouldBe("1");
        var body = LastBody;
        body.Root!.Name.ShouldBe(DavNs.CalDav + "calendar-query");
        var expand = body.Descendants(DavNs.CalDav + "expand").Single();
        expand.Attribute("start")!.Value.ShouldBe("20261116T000000Z");
        expand.Attribute("end")!.Value.ShouldBe("20261123T000000Z");
        var filters = body.Descendants(DavNs.CalDav + "comp-filter").ToList();
        filters.Select(f => f.Attribute("name")!.Value).ShouldBe(["VCALENDAR", "VEVENT"]);
        filters[1].Element(DavNs.CalDav + "time-range")!.Attribute("start")!.Value.ShouldBe("20261116T000000Z");
    }

    [Fact]
    public async Task Report_calendar_query_by_uid_without_data()
    {
        Respond("REPORT", Personal, 207, "<d:multistatus xmlns:d=\"DAV:\"/>");
        var result = await _dav.Client.ReportAsync(Personal, DavDepth.One, DavRequestBodies.CalendarQuery(new CalendarQuery("VEVENT") { Uid = "abc-123", IncludeData = false }), Ct);
        result.Value.Responses.ShouldBeEmpty();
        LastBody.Descendants(DavNames.CalendarData).ShouldBeEmpty();
        var match = LastBody.Descendants(DavNs.CalDav + "text-match").Single();
        match.Value.ShouldBe("abc-123");
        match.Attribute("collation")!.Value.ShouldBe("i;octet");
        match.Parent!.Attribute("name")!.Value.ShouldBe("UID");
    }

    [Fact]
    public async Task Report_calendar_multiget_with_a_missing_member()
    {
        Respond("REPORT", Personal, 207, Fixture.Read("multiget-partial.xml"));
        var hrefs = new[] { Personal + "0F3A1B2C-standup.ics", Personal + "gone%20event.ics" };

        var result = await _dav.Client.ReportAsync(Personal, DavDepth.One, DavRequestBodies.CalendarMultiget(hrefs), Ct);

        result.Value.Responses.Count(r => r.IsFound).ShouldBe(1);
        LastBody.Root!.Name.ShouldBe(DavNs.CalDav + "calendar-multiget");
        LastBody.Root.Elements(DavNames.Href).Select(h => h.Value).ShouldBe(hrefs);
    }

    [Fact]
    public async Task Report_addressbook_query_and_multiget()
    {
        const string book = "/remote.php/dav/addressbooks/users/alice/contacts/";
        Respond("REPORT", book, 207, Fixture.Read("addressbook-query.xml"));

        var query = await _dav.Client.ReportAsync(book, DavDepth.One, DavRequestBodies.AddressBookQuery("smith", limit: 25), Ct);
        query.Value.Responses.Single().AddressData.ShouldNotBeNull().ShouldContain("BEGIN:VCARD");
        var body = LastBody;
        body.Root!.Name.ShouldBe(DavNs.CardDav + "addressbook-query");
        body.Descendants(DavNs.CardDav + "prop-filter").Select(p => p.Attribute("name")!.Value).ShouldBe(["FN", "EMAIL"]);
        body.Descendants(DavNs.CardDav + "text-match").ShouldAllBe(t => t.Value == "smith" && t.Attribute("match-type")!.Value == "contains");
        body.Descendants(DavNs.CardDav + "nresults").Single().Value.ShouldBe("25");

        var multiget = await _dav.Client.ReportAsync(book, DavDepth.One, DavRequestBodies.AddressBookMultiget([book + "a.vcf"]), Ct);
        multiget.IsSuccess.ShouldBeTrue();
        LastBody.Root!.Name.ShouldBe(DavNs.CardDav + "addressbook-multiget");
        LastBody.Descendants(DavNames.AddressData).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sync_collection_splits_changes_deletions_and_truncation()
    {
        Respond("REPORT", Personal, 207, Fixture.Read("sync-collection.xml"));

        var result = await _dav.Client.SyncCollectionAsync(Personal, "http://sabre.io/ns/sync/40", [DavNames.GetETag], 100, Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SyncToken.ShouldBe("http://sabre.io/ns/sync/42");
        result.Value.Changed.Select(r => r.Href).ShouldBe([Personal + "new.ics", Personal + "changed.ics"]);
        result.Value.Changed[0].ETag.ShouldBe("\"aa\"");
        result.Value.Removed.ShouldBe([Personal + "deleted.ics"]);
        result.Value.IsTruncated.ShouldBeTrue();
        Header("Depth").ShouldBe("0");
        var body = LastBody;
        body.Root!.Name.ShouldBe(DavNs.Dav + "sync-collection");
        body.Root.Element(DavNames.SyncToken)!.Value.ShouldBe("http://sabre.io/ns/sync/40");
        body.Root.Element(DavNs.Dav + "sync-level")!.Value.ShouldBe("1");
        body.Descendants(DavNs.Dav + "nresults").Single().Value.ShouldBe("100");
    }

    [Fact]
    public async Task Sync_collection_initial_sends_an_empty_token()
    {
        Respond("REPORT", Personal, 207, "<d:multistatus xmlns:d=\"DAV:\"><d:sync-token>http://sabre.io/ns/sync/1</d:sync-token></d:multistatus>");
        var result = await _dav.Client.SyncCollectionAsync(Personal, null, [DavNames.GetETag], null, Ct);
        result.Value.SyncToken.ShouldBe("http://sabre.io/ns/sync/1");
        result.Value.IsTruncated.ShouldBeFalse();
        LastBody.Root!.Element(DavNames.SyncToken)!.Value.ShouldBeEmpty();
        LastBody.Descendants(DavNs.Dav + "limit").ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_collection_with_an_expired_token_is_sync_state_gone()
    {
        Respond("REPORT", Personal, 403, "<?xml version=\"1.0\"?><d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\"><d:valid-sync-token/><s:exception>Sabre\\DAV\\Exception\\InvalidSyncToken</s:exception><s:message>Invalid or unknown sync token</s:message></d:error>");
        var result = await _dav.Client.SyncCollectionAsync(Personal, "http://sabre.io/ns/sync/1", [DavNames.GetETag], null, Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public async Task Sync_collection_without_a_token_in_the_answer_is_unavailable()
    {
        Respond("REPORT", Personal, 207, "<d:multistatus xmlns:d=\"DAV:\"/>");
        var result = await _dav.Client.SyncCollectionAsync(Personal, null, [], null, Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Free_busy_query_returns_the_vfreebusy_text()
    {
        Respond("REPORT", Personal, 200, Fixture.Read("vfreebusy-query.ics"), "text/calendar; charset=utf-8");
        var result = await _dav.Client.FreeBusyQueryAsync(Personal, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero), Ct);
        result.Value.ShouldContain("BEGIN:VFREEBUSY");
        LastBody.Root!.Name.ShouldBe(DavNs.CalDav + "free-busy-query");
        LastBody.Descendants(DavNs.CalDav + "time-range").Single().Attribute("end")!.Value.ShouldBe("20261117T000000Z");
    }

    [Fact]
    public async Task Get_returns_body_etag_and_content_type()
    {
        Respond("GET", Event, 200, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "text/calendar; charset=utf-8", ("ETag", "\"e1\""));
        var result = await _dav.Client.GetAsync(Event, Ct);
        result.Value.Body.ShouldStartWith("BEGIN:VCALENDAR");
        result.Value.ETag.ShouldBe("\"e1\"");
        result.Value.ContentType!.ShouldStartWith("text/calendar");
        result.Value.Href.ShouldBe(Event);
    }

    [Fact]
    public async Task Open_read_streams_the_content()
    {
        Server.Given(Request.Create().WithPath("/remote.php/dav/files/alice/report.pdf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/pdf").WithHeader("ETag", "\"f\"").WithBody(new byte[] { 1, 2, 3 }));
        var result = await _dav.Client.OpenReadAsync("files/alice/report.pdf", Ct);
        using var download = result.Value;
        download.ContentType.ShouldBe("application/pdf");
        download.ETag.ShouldBe("\"f\"");
        using var copy = new MemoryStream();
        await download.Content.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Open_read_maps_errors()
    {
        Respond("GET", "/remote.php/dav/files/alice/missing", 404);
        (await _dav.Client.OpenReadAsync("files/alice/missing", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Put_create_only_sends_if_none_match_and_reports_created()
    {
        Respond("PUT", Event, 201, null, headers: ("ETag", "\"new\""));
        var result = await _dav.Client.PutAsync(Event, "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", "text/calendar; charset=utf-8", DavPrecondition.CreateOnly, Ct);
        result.Value.Created.ShouldBeTrue();
        result.Value.ETag.ShouldBe("\"new\"");
        Header("If-None-Match").ShouldBe("*");
        Header("If-Match").ShouldBeNull();
        Header("Content-Type").ShouldStartWith("text/calendar");
        LastRequest.RequestMessage!.Body.ShouldStartWith("BEGIN:VCALENDAR");
    }

    [Fact]
    public async Task Put_create_only_on_an_existing_item_is_a_conflict()
    {
        Respond("PUT", Event, 412);
        var result = await _dav.Client.PutAsync(Event, "x", "text/calendar", DavPrecondition.CreateOnly, Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Code.ShouldBe(DavErrors.ItemExistsCode);
    }

    [Fact]
    public async Task Put_if_match_sends_the_etag_and_tolerates_a_missing_etag()
    {
        Respond("PUT", Event, 204);
        var result = await _dav.Client.PutAsync(Event, "x", "text/calendar", DavPrecondition.Matching("\"old\""), Ct);
        result.Value.Created.ShouldBeFalse();
        result.Value.ETag.ShouldBeNull();
        Header("If-Match").ShouldBe("\"old\"");
        Header("If-None-Match").ShouldBeNull();
    }

    [Fact]
    public async Task Put_with_a_stale_etag_is_precondition_failed()
    {
        Respond("PUT", Event, 412);
        var result = await _dav.Client.PutAsync(Event, "x", "text/calendar", DavPrecondition.Matching("\"stale\""), Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    public async Task Put_uid_conflict_is_reported()
    {
        Respond("PUT", Event, 403, "<d:error xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><cal:no-uid-conflict><d:href>/remote.php/dav/calendars/alice/personal/other.ics</d:href></cal:no-uid-conflict></d:error>");
        var result = await _dav.Client.PutAsync(Event, "x", "text/calendar", DavPrecondition.None, Ct);
        result.Error!.Code.ShouldBe(DavErrors.UidConflictCode);
        Header("If-Match").ShouldBeNull();
        Header("If-None-Match").ShouldBeNull();
    }

    [Fact]
    public async Task Put_stream()
    {
        Respond("PUT", "/remote.php/dav/files/alice/a.bin", 201);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var result = await _dav.Client.PutAsync("files/alice/a.bin", stream, "application/octet-stream", DavPrecondition.None, Ct);
        result.Value.Created.ShouldBeTrue();
        LastRequest.RequestMessage!.Body.ShouldBe("payload");
    }

    [Fact]
    public async Task Delete_with_and_without_if_match()
    {
        Respond("DELETE", Event, 204);
        (await _dav.Client.DeleteAsync(Event, DavPrecondition.Matching("\"e\""), Ct)).IsSuccess.ShouldBeTrue();
        Header("If-Match").ShouldBe("\"e\"");
        (await _dav.Client.DeleteAsync(Event, DavPrecondition.None, Ct)).IsSuccess.ShouldBeTrue();
        Header("If-Match").ShouldBeNull();
    }

    [Fact]
    public async Task Delete_missing_is_not_found()
    {
        Respond("DELETE", Event, 404);
        (await _dav.Client.DeleteAsync(Event, DavPrecondition.None, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task MkCol_plain_and_extended()
    {
        Respond("MKCOL", "/remote.php/dav/files/alice/Attachments/", 201);
        (await _dav.Client.MkColAsync("files/alice/Attachments/", null, Ct)).IsSuccess.ShouldBeTrue();
        LastRequest.RequestMessage!.Body.ShouldBeNullOrEmpty();

        Respond("MKCOL", "/remote.php/dav/addressbooks/users/alice/work/", 201);
        var body = DavRequestBodies.ExtendedMkCol([DavNames.Collection, DavNames.AddressBook], [new XElement(DavNames.DisplayName, "Work")]);
        (await _dav.Client.MkColAsync("addressbooks/users/alice/work/", body, Ct)).IsSuccess.ShouldBeTrue();
        LastBody.Root!.Name.ShouldBe(DavNs.Dav + "mkcol");
        LastBody.Descendants(DavNames.ResourceType).Single().Elements().Select(e => e.Name).ShouldBe([DavNames.Collection, DavNames.AddressBook]);
    }

    [Fact]
    public async Task MkCol_on_an_existing_collection_is_a_conflict()
    {
        Respond("MKCOL", "/remote.php/dav/files/alice/Attachments/", 405);
        (await _dav.Client.MkColAsync("files/alice/Attachments/", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task MkCalendar_sends_name_color_and_components()
    {
        Respond("MKCALENDAR", "/remote.php/dav/calendars/alice/groceries/", 201);
        var result = await _dav.Client.MkCalendarAsync("calendars/alice/groceries/", "Groceries", ["VTODO"], "#00FF00", Ct);
        result.IsSuccess.ShouldBeTrue();
        var body = LastBody;
        body.Root!.Name.ShouldBe(DavNs.CalDav + "mkcalendar");
        body.Descendants(DavNames.DisplayName).Single().Value.ShouldBe("Groceries");
        body.Descendants(DavNames.CalendarColor).Single().Value.ShouldBe("#00FF00");
        body.Descendants(DavNames.Comp).Single().Attribute("name")!.Value.ShouldBe("VTODO");
    }

    [Fact]
    public async Task Move_sends_absolute_destination_and_overwrite()
    {
        Respond("MOVE", Event, 201);
        var result = await _dav.Client.MoveAsync(Event, "calendars/alice/work/standup.ics", overwrite: false, Ct);
        result.IsSuccess.ShouldBeTrue();
        Header("Destination").ShouldBe(Server.Url + "/remote.php/dav/calendars/alice/work/standup.ics");
        Header("Overwrite").ShouldBe("F");
    }

    [Theory]
    [InlineData(true, "LOCK")]
    [InlineData(false, "UNLOCK")]
    public async Task A_user_lock_is_lock_or_unlock_with_the_user_lock_header(bool locked, string method)
    {
        Respond(method, "/remote.php/dav/files/alice/a.txt", 200);

        (await _dav.Client.UserLockAsync("files/alice/a.txt", locked, Ct)).IsSuccess.ShouldBeTrue();

        Header("X-User-Lock").ShouldBe("1");
    }

    [Theory]
    [InlineData(423, ErrorKind.Conflict)]
    [InlineData(412, ErrorKind.PreconditionFailed)]
    public async Task A_refused_user_lock_keeps_its_reason(int status, ErrorKind kind)
    {
        Respond("UNLOCK", "/remote.php/dav/files/alice/a.txt", status);

        (await _dav.Client.UserLockAsync("files/alice/a.txt", locked: false, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Move_without_overwrite_onto_an_existing_item_is_a_conflict()
    {
        Respond("MOVE", Event, 412);
        (await _dav.Client.MoveAsync(Event, "calendars/alice/work/standup.ics", overwrite: false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Move_outside_the_root_is_refused_without_a_request()
    {
        var result = await _dav.Client.MoveAsync(Event, "/index.php", overwrite: true, Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Copy_sends_destination_overwrite_and_infinite_depth()
    {
        Respond("COPY", "/remote.php/dav/files/alice/a.txt", 201);
        (await _dav.Client.CopyAsync("files/alice/a.txt", "files/alice/b.txt", overwrite: true, Ct)).IsSuccess.ShouldBeTrue();
        Header("Destination").ShouldBe(Server.Url + "/remote.php/dav/files/alice/b.txt");
        Header("Overwrite").ShouldBe("T");
        Header("Depth").ShouldBe("infinity");
    }

    [Fact]
    public async Task Copy_without_overwrite_onto_an_existing_item_is_a_conflict_and_outside_the_root_is_refused()
    {
        Respond("COPY", "/remote.php/dav/files/alice/a.txt", 412);
        (await _dav.Client.CopyAsync("files/alice/a.txt", "files/alice/b.txt", overwrite: false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        Server.Reset();
        (await _dav.Client.CopyAsync("files/alice/a.txt", "/index.php", overwrite: false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_goes_to_the_dav_root()
    {
        Respond("SEARCH", "/remote.php/dav/", 207, "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/remote.php/dav/files/alice/a.txt</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>");

        var result = await _dav.Client.SearchAsync(new XDocument(new XElement(DavNs.Dav + "searchrequest")), Ct);

        result.Value.Responses.Single().Href.ShouldBe("/remote.php/dav/files/alice/a.txt");
        LastBody.Root!.Name.ShouldBe(DavNs.Dav + "searchrequest");
    }

    [Fact]
    public async Task Ocs_send_posts_a_form_and_maps_failures()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/files_sharing/api/v1/shares").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"ocs\":{\"meta\":{\"statuscode\":200},\"data\":{\"id\":\"9\"}}}"));
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/files_sharing/api/v1/shares/9").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"ocs\":{\"meta\":{\"statuscode\":404}}}"));

        var created = await _dav.Client.OcsSendAsync(HttpMethod.Post, "apps/files_sharing/api/v1/shares", new Dictionary<string, string> { ["path"] = "/a b.txt" }, Ct);

        created.Value.GetProperty("id").GetString().ShouldBe("9");
        Header("OCS-APIRequest").ShouldBe("true");
        LastRequest.RequestMessage!.Body.ShouldBe("path=%2Fa+b.txt");
        (await _dav.Client.OcsSendAsync(HttpMethod.Delete, "apps/files_sharing/api/v1/shares/9", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task PropPatch_success_and_atomic_failure()
    {
        const string ok = "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/remote.php/dav/calendars/alice/personal/</d:href><d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";
        Respond("PROPPATCH", Personal, 207, ok);
        var result = await _dav.Client.PropPatchAsync(Personal, [new XElement(DavNames.DisplayName, "Home")], [DavNames.CalendarColor], Ct);
        result.IsSuccess.ShouldBeTrue();
        LastBody.Root!.Name.ShouldBe(DavNs.Dav + "propertyupdate");
        LastBody.Descendants(DavNs.Dav + "set").Single().Descendants(DavNames.DisplayName).Single().Value.ShouldBe("Home");
        LastBody.Descendants(DavNs.Dav + "remove").Single().Descendants(DavNames.CalendarColor).ShouldHaveSingleItem();

        Server.Reset();
        const string failed = "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/remote.php/dav/calendars/alice/personal/</d:href><d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 424 Failed Dependency</d:status></d:propstat><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat></d:response></d:multistatus>";
        Respond("PROPPATCH", Personal, 207, failed);
        (await _dav.Client.PropPatchAsync(Personal, [new XElement(DavNames.ResourceType)], [], Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Post_to_outbox_parses_the_schedule_response()
    {
        const string outbox = "/remote.php/dav/calendars/alice/outbox/";
        Respond("POST", outbox, 200, Fixture.Read("schedule-response.xml"), "application/xml; charset=utf-8");

        var result = await _dav.Client.PostToOutboxAsync(outbox, "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nEND:VCALENDAR\r\n", Ct);

        result.Value.Count.ShouldBe(2);
        result.Value[0].Recipient.ShouldBe("mailto:bob@example.com");
        result.Value[0].IsSuccess.ShouldBeTrue();
        result.Value[0].CalendarData.ShouldNotBeNull().ShouldContain("FREEBUSY");
        result.Value[1].IsSuccess.ShouldBeFalse();
        result.Value[1].RequestStatus.ShouldStartWith("3.7");
        Header("Content-Type").ShouldStartWith("text/calendar");
    }

    [Fact]
    public async Task Post_to_outbox_rejects_unexpected_bodies()
    {
        const string outbox = "/remote.php/dav/calendars/alice/outbox/";
        Respond("POST", outbox, 200, "<d:multistatus xmlns:d=\"DAV:\"/>");
        (await _dav.Client.PostToOutboxAsync(outbox, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        Server.Reset();
        Respond("POST", outbox, 200, "<broken");
        (await _dav.Client.PostToOutboxAsync(outbox, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Ocs_get_returns_data_and_sends_the_ocs_header()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/files_sharing/api/v1/shares").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"ocs\":{\"meta\":{\"status\":\"ok\",\"statuscode\":200,\"message\":\"OK\"},\"data\":[{\"id\":\"7\",\"token\":\"abc\"}]}}"));

        var result = await _dav.Client.OcsGetAsync("apps/files_sharing/api/v1/shares", new Dictionary<string, string> { ["path"] = "/Docs/a b.pdf" }, Ct);

        result.Value[0].GetProperty("token").GetString().ShouldBe("abc");
        Header("OCS-APIRequest").ShouldBe("true");
        Header("Authorization").ShouldBe("Bearer " + DavTestServer.Token);
        LastRequest.RequestMessage!.Query!["format"].ShouldBe(["json"]);
        LastRequest.RequestMessage.Query["path"].ShouldBe(["/Docs/a b.pdf"]);
    }

    [Fact]
    public async Task Ocs_send_posts_a_form_and_takes_any_2xx_ocs_status()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json")
                .WithBody("{\"ocs\":{\"meta\":{\"status\":\"ok\",\"statuscode\":201,\"message\":\"OK\"},\"data\":{\"token\":\"abcd1234\"}}}"));

        var result = await _dav.Client.OcsSendAsync(HttpMethod.Post, "apps/spreed/api/v4/room", new Dictionary<string, string> { ["roomName"] = "Q3 review & plan" }, Ct);

        result.Value.GetProperty("token").GetString().ShouldBe("abcd1234");
        Header("OCS-APIRequest").ShouldBe("true");
        Header("Content-Type").ShouldStartWith("application/x-www-form-urlencoded");
        LastRequest.RequestMessage!.Body.ShouldBe("roomName=Q3+review+%26+plan");
        LastRequest.RequestMessage.Query!["format"].ShouldBe(["json"]);
    }

    [Fact]
    public async Task Ocs_send_maps_failures_and_refuses_paths_outside_the_root()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room/gone1234").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("{\"ocs\":{\"meta\":{\"statuscode\":404}}}"));

        (await _dav.Client.OcsSendAsync(HttpMethod.Delete, "apps/spreed/api/v4/room/gone1234", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _dav.Client.OcsSendAsync(HttpMethod.Post, "../index.php", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Ocs_exchange_sends_json_and_headers_and_returns_status_and_response_headers()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json")
                .WithHeader("Set-Cookie", "oc_session=abc; path=/; secure")
                .WithHeader("X-Chat-Last-Given", "42")
                .WithBody("{\"ocs\":{\"meta\":{\"status\":\"ok\",\"statuscode\":201,\"message\":\"OK\"},\"data\":{\"token\":\"abcd1234\"}}}"));

        var result = await _dav.Client.OcsExchangeAsync(
            new OcsRequest(HttpMethod.Post, "apps/spreed/api/v4/room")
            {
                Json = new System.Text.Json.Nodes.JsonObject { ["roomType"] = 2, ["participants"] = new System.Text.Json.Nodes.JsonObject { ["teams"] = new System.Text.Json.Nodes.JsonArray("t1") } },
                Query = new Dictionary<string, string> { ["x"] = "1" },
                Headers = new Dictionary<string, string> { ["Cookie"] = "oc_session=prev" },
            },
            Ct);

        result.Value.Status.ShouldBe(201);
        result.Value.Data.GetProperty("token").GetString().ShouldBe("abcd1234");
        result.Value.Header("x-chat-last-given").ShouldBe("42");
        result.Value.SetCookies.ShouldBe(["oc_session=abc; path=/; secure"]);
        Header("Content-Type").ShouldStartWith("application/json");
        Header("Cookie").ShouldBe("oc_session=prev");
        Header("OCS-APIRequest").ShouldBe("true");
        LastRequest.RequestMessage!.Body.ShouldBe("{\"roomType\":2,\"participants\":{\"teams\":[\"t1\"]}}");
        LastRequest.RequestMessage.Query!["x"].ShouldBe(["1"]);
        LastRequest.RequestMessage.Query["format"].ShouldBe(["json"]);
    }

    [Fact]
    public async Task Ocs_exchange_keeps_the_reason_talk_gives_for_a_failure()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("{\"ocs\":{\"meta\":{\"status\":\"failure\",\"statuscode\":400,\"message\":\"\"},\"data\":{\"error\":\"object-type\"}}}"));
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room/abcd1234").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"ocs\":{\"meta\":{\"status\":\"failure\",\"statuscode\":403,\"message\":\"Not a moderator\"},\"data\":[]}}"));
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v4/room/plain123").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(502).WithBody("<html>bad gateway</html>"));

        var refused = await _dav.Client.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, "apps/spreed/api/v4/room"), Ct);
        var forbidden = await _dav.Client.OcsExchangeAsync(OcsRequest.Get("apps/spreed/api/v4/room/abcd1234"), Ct);
        var gateway = await _dav.Client.OcsExchangeAsync(OcsRequest.Get("apps/spreed/api/v4/room/plain123"), Ct);

        refused.Error!.Kind.ShouldBe(ErrorKind.Validation);
        refused.Error.Message.ShouldEndWith("(object-type)");
        forbidden.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        forbidden.Error.Message.ShouldEndWith("(Not a moderator)");
        gateway.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await _dav.Client.OcsExchangeAsync(OcsRequest.Get("../x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Ocs_exchange_reads_a_bodiless_304_as_success_without_data()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/spreed/api/v1/chat/abcd1234").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("X-Chat-Last-Given", "7"));

        var result = await _dav.Client.OcsExchangeAsync(OcsRequest.Get("apps/spreed/api/v1/chat/abcd1234"), Ct);

        result.Value.Status.ShouldBe(304);
        result.Value.Data.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
        result.Value.Header("X-Chat-Last-Given").ShouldBe("7");
    }

    [Fact]
    public async Task Ocs_exchange_refuses_a_success_without_ocs_json()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/y").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<html>login</html>"));

        (await _dav.Client.OcsExchangeAsync(OcsRequest.Get("apps/y"), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Ocs_errors_map()
    {
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/x").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("{\"ocs\":{\"meta\":{\"statuscode\":404}}}"));
        (await _dav.Client.OcsGetAsync("apps/x", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/y").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"ocs\":{\"meta\":{\"statuscode\":998}}}"));
        (await _dav.Client.OcsGetAsync("apps/y", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/z").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("not json"));
        (await _dav.Client.OcsGetAsync("apps/z", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        (await _dav.Client.OcsGetAsync("../index.php", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Theory]
    [InlineData(400, ErrorKind.Validation)]
    [InlineData(401, ErrorKind.BackendUnavailable)] // the test credential is token-exchanged (ADR 0001 §42)
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(409, ErrorKind.Conflict)]
    [InlineData(412, ErrorKind.PreconditionFailed)]
    [InlineData(413, ErrorKind.PayloadTooLarge)]
    [InlineData(415, ErrorKind.Validation)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    [InlineData(507, ErrorKind.PayloadTooLarge)]
    public async Task Http_errors_map_to_error_kinds(int status, ErrorKind kind)
    {
        Respond("PROPFIND", Personal, status, "<?xml version=\"1.0\"?><d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\"><s:message>nope</s:message></d:error>");
        var result = await _dav.Client.PropFindAsync(Personal, DavDepth.Zero, [DavNames.DisplayName], Ct);
        result.Error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(CredentialStrategy.PassThrough, ErrorKind.BackendAuthFailed)]
    [InlineData(CredentialStrategy.TokenExchange, ErrorKind.BackendUnavailable)]
    public async Task A_401_means_reauthenticate_only_for_a_passed_through_token(CredentialStrategy strategy, ErrorKind kind)
    {
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Bearer", DavTestServer.Token, strategy)));
        var client = new DavClient(_dav.Http, _dav.Credentials, _dav.Paths, _dav.Time);
        Respond("GET", Event, 401, null);
        Server.Given(Request.Create().WithPath("/ocs/v2.php/apps/denied").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"ocs\":{\"meta\":{\"statuscode\":997},\"data\":[]}}"));

        (await client.GetAsync(Event, Ct)).Error!.Kind.ShouldBe(kind);
        (await client.OcsGetAsync("apps/denied", null, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    public async Task Throttling_and_unavailability_carry_retry_after(int status, ErrorKind kind)
    {
        Respond("GET", Event, status, null, headers: ("Retry-After", "42"));
        var result = await _dav.Client.GetAsync(Event, Ct);
        result.Error!.Kind.ShouldBe(kind);
        result.Error.RetryAfterSeconds.ShouldBe(42);
    }

    [Fact]
    public async Task Malformed_multistatus_is_backend_unavailable()
    {
        Respond("PROPFIND", Personal, 207, "<d:multistatus xmlns:d=\"DAV:\"><d:response>");
        (await _dav.Client.PropFindAsync(Personal, DavDepth.Zero, [], Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Hrefs_outside_the_root_are_refused_without_a_request()
    {
        (await _dav.Client.GetAsync("/index.php/apps/files", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _dav.Client.PropFindAsync("../../etc", DavDepth.Zero, [], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _dav.Client.PutAsync("https://evil.example/remote.php/dav/x", "x", "text/plain", DavPrecondition.None, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_timeout_is_backend_unavailable()
    {
        Server.Given(Request.Create().WithPath(Event).UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)));
        using var http = new HttpClient(new Nightware.Adapters.Dav.Core.Http.DavAuthenticationHandler { InnerHandler = new HttpClientHandler() }) { Timeout = TimeSpan.FromMilliseconds(150) };
        var client = new DavClient(http, _dav.Credentials, _dav.Paths, _dav.Time);
        (await client.GetAsync(Event, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_unreachable_server_is_backend_unavailable()
    {
        using var dead = new DavTestServer();
        dead.Server.Stop();
        (await dead.Client.GetAsync(Event, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Caller_cancellation_still_throws()
    {
        Server.Given(Request.Create().WithPath(Event).UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(100);
        await Should.ThrowAsync<OperationCanceledException>(() => _dav.Client.GetAsync(Event, cts.Token));
    }

    [Fact]
    public async Task Credential_failures_are_returned_without_a_request()
    {
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Failure(Error.BackendAuthFailed("token exchange failed")));
        (await _dav.Client.GetAsync(Event, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Impersonation_is_unsupported()
    {
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Basic", "svc:secret", CredentialStrategy.ServiceAccount, "bob@example.com")));
        (await _dav.Client.GetAsync(Event, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_credential_is_fetched_once_per_client()
    {
        Respond("GET", Event, 200, "x", "text/plain");
        await _dav.Client.GetAsync(Event, Ct);
        await _dav.Client.GetAsync(Event, Ct);
        await _dav.Credentials.Received(1).ForAsync(Backend.Groupware, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Works_under_a_nextcloud_sub_path()
    {
        using var dav = new DavTestServer("/nextcloud");
        dav.Server.Given(Request.Create().WithPath("/nextcloud/remote.php/dav/calendars/alice/x.ics").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));
        (await dav.Client.GetAsync("/nextcloud/remote.php/dav/calendars/alice/x.ics", Ct)).Value.Body.ShouldBe("ok");
        (await dav.Client.GetAsync("calendars/alice/x.ics", Ct)).Value.Body.ShouldBe("ok");
    }

    public void Dispose() => _dav.Dispose();
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Calendar;

public sealed class EventEndpointsTests : IAsyncLifetime
{
    private static readonly EventId Id = new("ev-1");
    private readonly ICalendarReader _reader = Substitute.For<ICalendarReader>();
    private readonly ICalendarWriter _writer = Substitute.For<ICalendarWriter>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_reader).AddSingleton(_writer));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static CalendarEvent Review() =>
        new(Id, "Review", new DateTimeTimeZone(new DateTime(2026, 6, 10, 19, 0, 0), "Australia/Sydney"), new DateTimeTimeZone(new DateTime(2026, 6, 10, 20, 0, 0), "Australia/Sydney"))
        {
            ICalUId = "uid-1",
            IsOrganizer = true,
            ResponseStatus = new ResponseStatus(ResponseType.Organizer),
            Organizer = new EmailAddress("me@example.com"),
            Attendees = [new Attendee(new EmailAddress("sam@example.com"), AttendeeType.Required, new ResponseStatus(ResponseType.Accepted, new DateTimeOffset(2026, 6, 2, 1, 0, 0, TimeSpan.Zero)))],
            Recurrence = new PatternedRecurrence(
                new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Wednesday] },
                new RecurrenceRange(RecurrenceRangeType.Numbered, new DateOnly(2026, 6, 10)) { NumberOfOccurrences = 3 }),
            Type = EventType.SeriesMaster,
        };

    private static HttpRequestMessage Get(string path, bool preferUtc = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (preferUtc)
        {
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }

        return request;
    }

    [Theory]
    [InlineData("/v1.0/me", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task Calendar_view_converts_to_the_preferred_zone_in_both_forms(string prefix, string mailbox)
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        _reader.CalendarViewAsync(new MailboxRef(mailbox), start, start.AddMonths(1), Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
            .Returns(new Page<CalendarEvent>([Review()], "100"));

        var response = await _host.AuthorizedClient().SendAsync(
            Get($"{prefix}/calendarView?startDateTime=2026-06-01T00:00:00.000Z&endDateTime=2026-07-01T00:00:00.000Z&$top=100&$select=id,start,end", preferUtc: true), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Preference-Applied").ShouldHaveSingleItem().ShouldBe("outlook.timezone=\"UTC\"");
        var body = await response.ReadObjectAsync();
        var ev = body["value"]![0]!;
        ev.Str("start.dateTime").ShouldBe("2026-06-10T09:00:00.0000000");
        ev.Str("start.timeZone").ShouldBe("UTC");
        ev.AsObject().Select(p => p.Key).ShouldBe(["id", "start", "end"], ignoreOrder: true);
        body["@odata.nextLink"]!.GetValue<string>().ShouldContain("$skiptoken=100");
    }

    [Theory]
    [InlineData("endDateTime=2026-07-01T00:00:00Z")]
    [InlineData("startDateTime=2026-06-01T00:00:00Z")]
    [InlineData("startDateTime=yesterday&endDateTime=2026-07-01T00:00:00Z")]
    [InlineData("startDateTime=2026-07-01T00:00:00Z&endDateTime=2026-06-01T00:00:00Z")]
    public async Task Calendar_view_needs_a_valid_window(string query)
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/calendarView?" + query, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Theory]
    [InlineData("/v1.0/me")]
    [InlineData("/v1.0/users/shared@example.com")]
    public async Task Calendar_view_refuses_a_window_over_ten_years_in_both_forms(string prefix)
    {
        var response = await _host.AuthorizedClient().GetAsync($"{prefix}/calendarView?startDateTime=1900-01-01T00:00:00.000Z&endDateTime=2900-01-01T00:00:00.000Z", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("invalidRequest");
        body.Str("error.message")!.ShouldContain("3653 days");
        await _reader.DidNotReceiveWithAnyArgs().CalendarViewAsync(default, default, default, null!, Ct);
    }

    [Fact]
    public async Task Calendar_view_serves_the_out_of_office_sweeps_longest_window()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _reader.CalendarViewAsync(new MailboxRef("me@example.com"), start, start.AddYears(6), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Page<CalendarEvent>([Review()]));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/calendarView?startDateTime=2025-01-01T00:00:00.000Z&endDateTime=2031-01-01T00:00:00.000Z&$top=100", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/v1.0/me/events/ev-1", "me@example.com")]
    [InlineData("/v1.0/users/shared%40example.com/events/ev-1", "shared@example.com")]
    public async Task Get_returns_the_full_event_in_its_own_zone_without_prefer(string path, string mailbox)
    {
        _reader.GetAsync(new MailboxRef(mailbox), Id, Arg.Any<CancellationToken>()).Returns(Review());

        var body = await (await _host.AuthorizedClient().GetAsync(path, Ct)).ReadObjectAsync();

        body.Str("iCalUId").ShouldBe("uid-1");
        body.Str("start.timeZone").ShouldBe("Australia/Sydney");
        body.Str("responseStatus.response").ShouldBe("organizer");
        body.Str("responseStatus.time").ShouldBe("0001-01-01T00:00:00Z");
        body.Str("organizer.emailAddress.name").ShouldBe("me@example.com");
        body.Str("location.displayName").ShouldBe(string.Empty);
        body.Str("recurrence.pattern.type").ShouldBe("weekly");
        body.Str("recurrence.pattern.index").ShouldBe("first");
        body.Str("recurrence.range.endDate").ShouldBe("0001-01-01");
        body.Str("recurrence.range.recurrenceTimeZone").ShouldBe("AUS Eastern Standard Time");
        body["recurrence"]!["range"]!["numberOfOccurrences"]!.GetValue<int>().ShouldBe(3);
        body["onlineMeeting"].ShouldBeNull();
        body.ContainsKey("onlineMeeting").ShouldBeTrue();
    }

    [Fact]
    public async Task All_day_events_keep_their_date_under_prefer()
    {
        _reader.GetAsync(new MailboxRef(TestHost.UserEmail), Id, Arg.Any<CancellationToken>()).Returns(Review() with
        {
            IsAllDay = true,
            Start = new DateTimeTimeZone(new DateTime(2026, 6, 10), "UTC"),
            End = new DateTimeTimeZone(new DateTime(2026, 6, 11), "UTC"),
        });
        var request = Get("/v1.0/me/events/ev-1");
        request.Headers.Add("Prefer", "outlook.timezone=\"Australia/Sydney\"");

        var body = await (await _host.AuthorizedClient().SendAsync(request, Ct)).ReadObjectAsync();

        body.Str("start.dateTime").ShouldBe("2026-06-10T00:00:00.0000000");
        body.Str("start.timeZone").ShouldBe("Australia/Sydney");
    }

    [Fact]
    public async Task Port_errors_become_graph_errors()
    {
        _reader.GetAsync(Arg.Any<MailboxRef>(), Id, Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/events/ev-1", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/v1.0/me/events", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/events", "shared@example.com")]
    public async Task List_by_icaluid_passes_the_filter_to_the_reader(string path, string mailbox)
    {
        _reader.ListAsync(new MailboxRef(mailbox), Arg.Is<ItemQuery>(q => q.Top == 1 && q.Filter is FilterNode.Compare), Arg.Any<CancellationToken>())
            .Returns(new Page<CalendarEvent>([Review()]));

        var body = await (await _host.AuthorizedClient().GetAsync(path + "?$filter=iCalUId eq 'it''s'&$select=id,isOrganizer&$top=1", Ct)).ReadObjectAsync();

        body["value"]![0]!.AsObject().Select(p => p.Key).ShouldBe(["id", "isOrganizer"], ignoreOrder: true);
        await _reader.Received(1).ListAsync(new MailboxRef(mailbox), Arg.Is<ItemQuery>(q => ((FilterNode.Compare)q.Filter!).Value == new FilterValue.Text("it's")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/v1.0/me/events", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/events", "shared@example.com")]
    public async Task Create_maps_the_nightmail_body_and_answers_201(string path, string mailbox)
    {
        NewEvent? sent = null;
        _writer.CreateAsync(new MailboxRef(mailbox), Arg.Do<NewEvent>(e => sent = e), Arg.Any<CancellationToken>()).Returns(Review());
        const string json = """
            {"subject":"Review","isAllDay":false,"isReminderOn":true,"reminderMinutesBeforeStart":10,
             "isOnlineMeeting":true,"onlineMeetingProvider":"teamsForBusiness",
             "body":{"contentType":"text","content":"Line 1\n<b>2</b>"},
             "start":{"dateTime":"2026-06-10T19:00:00","timeZone":"Australia/Sydney"},
             "end":{"dateTime":"2026-06-10T20:00:00","timeZone":"Australia/Sydney"},
             "locations":[{"displayName":"Boardroom","locationEmailAddress":"room@example.com","locationType":"conferenceRoom"}],
             "location":{"displayName":"Boardroom","locationEmailAddress":"room@example.com","locationType":"conferenceRoom"},
             "attendees":[{"emailAddress":{"address":"sam@example.com"},"type":"required"},{"emailAddress":{"address":"room@example.com","name":"Boardroom"},"type":"resource"}],
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["wednesday"]},"range":{"type":"numbered","startDate":"2026-06-10","numberOfOccurrences":3}}}
            """;

        var response = await _host.AuthorizedClient().PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync()).Str("id").ShouldBe("ev-1");
        sent!.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 6, 10, 19, 0, 0), "Australia/Sydney"));
        sent.BodyHtml.ShouldBe("Line 1<br>&lt;b&gt;2&lt;/b&gt;");
        sent.ReminderMinutesBeforeStart.ShouldBe(10);
        sent.Location!.LocationEmailAddress.ShouldBe("room@example.com");
        sent.Attendees.Select(a => a.Type).ShouldBe([AttendeeType.Required, AttendeeType.Resource]);
        sent.Recurrence!.Range.NumberOfOccurrences.ShouldBe(3);
        sent.Recurrence.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Wednesday]);
    }

    [Theory]
    [InlineData("""{"subject":"no times"}""")]
    [InlineData("""{"start":{"dateTime":"2026-06-10T19:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T18:00:00","timeZone":"UTC"}}""")]
    [InlineData("""{"start":{"dateTime":"2026-06-10T19:00:00","timeZone":"Mars/Base"},"end":{"dateTime":"2026-06-10T20:00:00","timeZone":"UTC"}}""")]
    [InlineData("""{"start":{"dateTime":"2026-06-10T19:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T20:00:00","timeZone":"UTC"},"attendees":[{"type":"required"}]}""")]
    [InlineData("""{"start":{"dateTime":"2026-06-10T19:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T20:00:00","timeZone":"UTC"},"recurrence":{"pattern":{"type":"daily"},"range":{"type":"endDate","startDate":"2026-06-10"}}}""")]
    [InlineData("not json")]
    public async Task Create_rejects_bad_bodies_with_400(string json)
    {
        var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/events", new StringContent(json, Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _writer.DidNotReceiveWithAnyArgs().CreateAsync(default, null!, Ct);
    }

    [Theory]
    [InlineData("/v1.0/me/events/ev-1", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/events/ev-1", "shared@example.com")]
    public async Task Patch_sends_only_what_the_body_carries(string path, string mailbox)
    {
        EventPatch? sent = null;
        _writer.UpdateAsync(new MailboxRef(mailbox), Id, Arg.Do<EventPatch>(p => sent = p), Arg.Any<CancellationToken>()).Returns(Review());
        var json = """{"start":{"dateTime":"2026-06-10T10:00:00.000","timeZone":"UTC"},"end":{"dateTime":"2026-06-10T11:00:00.000","timeZone":"UTC"},"location":{"displayName":""},"attendees":[]}""";

        var response = await _host.AuthorizedClient().PatchAsync(path + "?$select=id,subject", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync()).Select(p => p.Key).ShouldBe(["@odata.context", "id", "subject"], ignoreOrder: true);
        sent!.Start.ShouldBe(DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)));
        sent.Subject.ShouldBeNull();
        sent.Location!.DisplayName.ShouldBeEmpty();
        sent.Attendees.ShouldBeEmpty();
        sent.Recurrence.ShouldBeNull();
        sent.ClearRecurrence.ShouldBeFalse("an absent recurrence is left alone");
    }

    [Fact]
    public async Task Patch_with_a_null_recurrence_clears_it()
    {
        EventPatch? sent = null;
        _writer.UpdateAsync(new MailboxRef("me@example.com"), Id, Arg.Do<EventPatch>(p => sent = p), Arg.Any<CancellationToken>()).Returns(Review());

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/events/ev-1", new StringContent("""{"recurrence":null}""", Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (sent!.ClearRecurrence, sent.Recurrence, sent.ChangesSchedule).ShouldBe((true, null, true));
    }

    [Theory]
    [InlineData("/v1.0/me/events/ev-1", "me@example.com")]
    [InlineData("/v1.0/users/shared@example.com/events/ev-1", "shared@example.com")]
    public async Task Delete_answers_204_or_the_error(string path, string mailbox)
    {
        _writer.DeleteAsync(new MailboxRef(mailbox), Id, Arg.Any<CancellationToken>()).Returns(Result.Success(), Error.Forbidden("no"));

        (await _host.AuthorizedClient().DeleteAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _host.AuthorizedClient().DeleteAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

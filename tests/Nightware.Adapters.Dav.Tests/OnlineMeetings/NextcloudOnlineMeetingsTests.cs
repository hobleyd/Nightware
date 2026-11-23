using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Tests.Talk;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.Tests.OnlineMeetings;

/// <summary>Online meetings as Talk rooms (ADR 0009 D7) against a WireMock Nextcloud.</summary>
public sealed class NextcloudOnlineMeetingsTests : IDisposable
{
    private const string Rooms = TalkAdapterHarness.Rooms;
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TalkAdapterHarness _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOnlineMeetingStore Store => _talk.Get<IOnlineMeetingStore>();

    [Fact]
    public async Task A_meeting_is_read_from_the_room_its_participants_and_its_first_message()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());

        var meeting = (await Store.GetAsync("t.abcd1234", Ct)).Value;

        meeting.Id.ShouldBe("t.abcd1234");
        meeting.JoinWebUrl.ShouldBe(TalkAdapterHarness.Public + "call/abcd1234");
        meeting.ChatThreadId.ShouldBe("abcd1234");
        meeting.JoinMeetingId.ShouldBe("abcd1234");
        meeting.Subject.ShouldBe("Q3 review");
        meeting.Start.ShouldBe(Start);
        meeting.End.ShouldBe(Start.AddHours(1));
        meeting.Created.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795427296));
        meeting.Organizer.ShouldBe(new MeetingParticipant("alice-id", "Alice Anderson", "alice@example.com", MeetingRole.Presenter));
        meeting.Attendees.ShouldBe([
            new MeetingParticipant("bob-id", "Bob Brown", "bob@example.com", MeetingRole.Attendee),
            new MeetingParticipant(null, "Carol Guest", null, MeetingRole.Attendee),
        ]);

        // Reading never moves alice's read marker or clears her notifications.
        var chat = _talk.Requests("GET", TalkAdapterHarness.Chat + "abcd1234").Single().RequestMessage!.Query!;
        chat["setReadMarker"].Single().ShouldBe("0");
        chat["markNotificationsAsRead"].Single().ShouldBe("0");
        chat["lookIntoFuture"].Single().ShouldBe("1");
    }

    [Fact]
    public async Task An_unbound_room_has_no_span_and_a_trimmed_history_no_creation_time()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(objectType: "", objectId: ""));
        _talk.Ocs("GET", Rooms + "/abcd1234/participants", 200, "[]");
        _talk.Ocs("GET", TalkAdapterHarness.Chat + "abcd1234", 200, """[{"id":90,"timestamp":1795427296,"systemMessage":"","message":"hi"}]""");

        var meeting = (await Store.GetAsync("t.abcd1234", Ct)).Value;

        meeting.Start.ShouldBeNull();
        meeting.End.ShouldBeNull();
        meeting.Created.ShouldBeNull();
        meeting.Organizer.ShouldBeNull();
    }

    [Theory]
    [InlineData(2, 1)] // a group chat is no meeting
    [InlineData(3, 0)] // a public room alice doesn't take part in
    public async Task Other_rooms_are_not_meetings(int type, int participantType)
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(type: type, participantType: participantType));

        (await Store.GetAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("abcd1234")]
    [InlineData("t.UPPER")]
    [InlineData("t.ab")]
    public async Task Ids_this_adapter_did_not_issue_are_not_found_without_a_request(string id)
    {
        (await Store.GetAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.DeleteAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_room_is_not_found_and_an_outage_is_reported()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 404, "null");
        _talk.Ocs("GET", Rooms + "/efgh5678", 503, "null");

        (await Store.GetAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.GetAsync("t.efgh5678", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Create_opens_a_public_room_bound_to_the_span_with_the_known_attendees_and_its_event()
    {
        _talk.Ocs("POST", Rooms, 201, TalkAdapterHarness.Room());
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        _talk.Dav("PUT", "/cloud/remote.php/dav/calendars/alice/personal/*", 201, "");

        var meeting = await Store.CreateAsync(new NewOnlineMeeting("Q3 review", Start, Start.AddHours(1), ["bob-id", "stranger@example.com"]), Ct);

        meeting.Value.Id.ShouldBe("t.abcd1234");
        var body = JsonNode.Parse(_talk.Requests("POST", Rooms).Single().RequestMessage!.Body!)!;
        body.ToJsonString().ShouldBe("""{"roomType":3,"roomName":"Q3 review","objectType":"event","objectId":"1796115600#1796119200","participants":{"users":["bob"]}}""");

        // The meeting's record in alice's default calendar: Talk follows it by the link in LOCATION. Nobody is invited.
        var put = _talk.Requests("PUT").Single().RequestMessage!;
        put.Path.ShouldStartWith(TalkAdapterHarness.AliceCalendar);
        put.Headers!["If-None-Match"].Single().ShouldBe("*");
        var ics = Unfold(put.Body!);
        ics.ShouldContain("SUMMARY:Q3 review");
        ics.ShouldContain("DTSTART:20261201T090000Z");
        ics.ShouldContain("DTEND:20261201T100000Z");
        ics.ShouldContain("LOCATION:" + TalkAdapterHarness.Public + "call/abcd1234");
        ics.ShouldContain("URL:" + TalkAdapterHarness.Public + "call/abcd1234");
        ics.ShouldContain("X-NIGHTWARE-ONLINE-MEETING:abcd1234");
        ics.ShouldNotContain("ATTENDEE");
        ics.ShouldNotContain("ORGANIZER");
        _talk.Server.LogEntries.Select(e => e.RequestMessage!.Method).ShouldBe(["POST", "PUT", "GET", "GET", "GET"]);
    }

    [Fact]
    public async Task Create_closes_the_room_again_when_its_event_cannot_be_written()
    {
        _talk.Ocs("POST", Rooms, 201, TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234", 200, "null");
        _talk.Dav("PUT", "/cloud/remote.php/dav/calendars/alice/personal/*", 507, "");

        var result = await Store.CreateAsync(new NewOnlineMeeting("Q3 review", Start, Start.AddHours(1), []), Ct);

        result.IsSuccess.ShouldBeFalse();
        _talk.Requests("DELETE", Rooms + "/abcd1234").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Create_without_a_span_or_attendees_opens_an_unbound_untitled_room()
    {
        _talk.Ocs("POST", Rooms, 201, TalkAdapterHarness.Room());
        _talk.ServeMeeting(TalkAdapterHarness.Room(objectType: "", objectId: ""));

        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, []), Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("POST", Rooms).Single().RequestMessage!.Body.ShouldBe("""{"roomType":3,"roomName":"Online meeting"}""");
        _talk.Requests("PUT").ShouldBeEmpty(); // no span, no event
    }

    [Fact]
    public async Task Create_fails_when_the_directory_or_talk_does()
    {
        _talk.Names.AccountNameOfAsync("flaky", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("directory down"));
        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, ["flaky"]), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _talk.Ocs("POST", Rooms, 201, """{"token":"NOT A TOKEN"}""");
        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, []), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_rename_changes_the_meetings_event_and_talk_renames_the_room_from_it()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        ServeEvents("LOCATION", Event("abcd1234", record: true));

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4 review", null, null), Ct)).IsSuccess.ShouldBeTrue();

        // Talk refuses to rename a bound room (400 "event"): the room is never unbound or renamed over OCS.
        _talk.Requests("DELETE").ShouldBeEmpty();
        _talk.Requests("PUT", Rooms + "/abcd1234").ShouldBeEmpty();
        var put = _talk.Requests("PUT", EventHref).Single().RequestMessage!;
        put.Headers!["If-Match"].Single().ShouldBe("\"e1\"");
        var ics = Unfold(put.Body!);
        ics.ShouldContain("SUMMARY:Q4 review");
        ics.ShouldContain("LOCATION:" + TalkAdapterHarness.Public + "call/abcd1234");
        ics.ShouldContain("DTSTART:20261201T090000Z");
        _talk.Server.LogEntries.Select(e => e.RequestMessage!.Method).ShouldBe(["GET", "REPORT", "PUT", "GET", "GET", "GET"]);
    }

    [Fact]
    public async Task A_changed_span_moves_the_event_and_the_same_span_or_subject_changes_nothing()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        ServeEvents("LOCATION", Event("abcd1234", record: true));

        await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q3 review", Start, Start.AddHours(1)), Ct);
        _talk.Requests("REPORT").ShouldBeEmpty();
        _talk.Requests("PUT").ShouldBeEmpty();

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(false, null, Start.AddDays(1), Start.AddDays(1).AddHours(2)), Ct)).IsSuccess.ShouldBeTrue();
        var ics = Unfold(_talk.Requests("PUT", EventHref).Single().RequestMessage!.Body!);
        ics.ShouldContain("DTSTART:20261202T090000Z");
        ics.ShouldContain("DTEND:20261202T110000Z");
        ics.ShouldContain("SUMMARY:Q3 review");
        _talk.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_calendar_event_made_online_before_the_link_was_in_its_location_is_found_by_url_and_gains_it()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        ServeEvents("LOCATION");
        ServeEvents("URL", Event("abcd1234", record: false, location: "Boardroom", linkInLocation: false));

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4 review", null, null), Ct)).IsSuccess.ShouldBeTrue();

        var ics = Unfold(_talk.Requests("PUT", EventHref).Single().RequestMessage!.Body!);
        ics.ShouldContain("LOCATION:Boardroom\\, " + TalkAdapterHarness.Public + "call/abcd1234");
        ics.ShouldContain("SUMMARY:Q4 review");
        ics.ShouldContain("ATTENDEE;CN=Bob Brown"); // bob stays invited; Nextcloud sends him the new subject and place
        // Birthdays are read-only, so only her personal calendar is searched, for each property.
        _talk.Requests("REPORT").Select(r => r.RequestMessage!.Path).ShouldBe([TalkAdapterHarness.AliceCalendar, TalkAdapterHarness.AliceCalendar]);
    }

    [Fact]
    public async Task A_bound_meeting_without_an_event_gets_one_with_what_does_not_change()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        ServeEvents("LOCATION", Event("abcd12345", record: true)); // a text-match is a substring match: another room
        ServeEvents("URL");
        _talk.Dav("PUT", "/cloud/remote.php/dav/calendars/alice/personal/*", 201, "");

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(false, null, Start.AddDays(1), Start.AddDays(1).AddHours(2)), Ct)).IsSuccess.ShouldBeTrue();

        var put = _talk.Requests("PUT").Single().RequestMessage!;
        put.Path.ShouldNotBe(EventHref);
        put.Headers!["If-None-Match"].Single().ShouldBe("*");
        var ics = Unfold(put.Body!);
        ics.ShouldContain("SUMMARY:Q3 review");
        ics.ShouldContain("DTSTART:20261202T090000Z");
        ics.ShouldContain("X-NIGHTWARE-ONLINE-MEETING:abcd1234");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Only_the_owner_can_rename_or_re_time_a_bound_meeting(int participantType)
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room(participantType: participantType));

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4", null, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _talk.Requests("REPORT").ShouldBeEmpty();
        _talk.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_calendar_search_or_event_write_failure_is_reported()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        _talk.Dav("REPORT", TalkAdapterHarness.AliceCalendar, 503, "");
        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4", null, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _talk.Server.ResetMappings();
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        ServeEvents("LOCATION", Event("abcd1234", record: true));
        _talk.Dav("PUT", EventHref, 412, "");
        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4", null, null), Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unbound_room_is_renamed_directly_and_a_failed_rename_is_reported()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room(objectType: "", objectId: ""));
        _talk.Ocs("PUT", Rooms + "/abcd1234", 403, """{"error":"permissions"}""");

        var result = await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4", null, null), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _talk.Requests("PUT", Rooms + "/abcd1234").Single().RequestMessage!.Body.ShouldBe("roomName=Q4");
        _talk.Requests("DELETE").ShouldBeEmpty();
        _talk.Requests("REPORT").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unbound_room_cannot_gain_a_span()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room(objectType: "", objectId: ""));

        var result = await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(false, null, Start, Start.AddHours(1)), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _talk.Server.LogEntries.Select(e => e.RequestMessage!.Method).ShouldBe(["GET"]);
    }

    [Fact]
    public async Task Delete_deletes_the_meetings_event_and_closes_the_room()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234", 200, "null");
        ServeEvents("LOCATION", Event("abcd1234", record: true));
        _talk.Dav("DELETE", EventHref, 204, "");

        (await Store.DeleteAsync("t.abcd1234", Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("DELETE", EventHref).Single().RequestMessage!.Headers!["If-Match"].Single().ShouldBe("\"e1\"");
        _talk.Requests("DELETE", Rooms + "/abcd1234").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_leaves_a_calendar_meeting_made_online_and_closes_its_room()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234", 200, "null");
        ServeEvents("LOCATION", Event("abcd1234", record: false));

        (await Store.DeleteAsync("t.abcd1234", Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("DELETE", EventHref).ShouldBeEmpty();
        _talk.Requests("DELETE", Rooms + "/abcd1234").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_by_a_participant_who_is_not_the_owner_only_closes_the_room()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(participantType: 2));
        _talk.Ocs("DELETE", Rooms + "/abcd1234", 200, "null");

        (await Store.DeleteAsync("t.abcd1234", Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("REPORT").ShouldBeEmpty();
        _talk.Requests("DELETE", Rooms + "/abcd1234").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_of_a_room_that_is_not_a_meeting_touches_nothing()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(type: 1));

        (await Store.DeleteAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests("DELETE").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TalkAdapterHarness.Public + "call/abcd1234")]
    [InlineData("HTTPS://CLOUD.EXAMPLE.COM/cloud/call/abcd1234/")]
    [InlineData("https%3A%2F%2Fcloud.example.com%2Fcloud%2Fcall%2Fabcd1234")]
    public async Task Find_reads_the_room_a_join_link_names(string url)
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());

        (await Store.FindByJoinUrlAsync(url, Ct)).Value.Id.ShouldBe("t.abcd1234");
    }

    [Fact]
    public async Task Find_of_another_link_is_not_found_without_a_request()
    {
        (await Store.FindByJoinUrlAsync("https://teams.microsoft.com/l/meetup-join/x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Server.LogEntries.ShouldBeEmpty();
    }

    private const string EventHref = TalkAdapterHarness.AliceCalendar + "meeting.ics";

    /// <summary>Answers the calendar-query for events whose <paramref name="property"/> holds a room link with <paramref name="events"/>.</summary>
    private void ServeEvents(string property, params string[] events)
    {
        var responses = string.Concat(events.Select(ics =>
            $"""<d:response><d:href>{EventHref}</d:href><d:propstat><d:prop><d:getetag>"e1"</d:getetag><cal:calendar-data>{System.Security.SecurityElement.Escape(ics)}</cal:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"""));
        _talk.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(TalkAdapterHarness.AliceCalendar).UsingMethod("REPORT")
                .WithBody(new WireMock.Matchers.WildcardMatcher($"*prop-filter name=\"{property}\"*")))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(207).WithHeader("Content-Type", "application/xml; charset=utf-8")
                .WithBody($"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:cal="urn:ietf:params:xml:ns:caldav">{responses}</d:multistatus>"""));
        _talk.Dav("PUT", EventHref, 204, "");
    }

    /// <summary>
    /// A stored event for room <paramref name="token"/>: the meeting's record (marked, nobody invited), or alice's calendar
    /// meeting with bob, made online (with the link in LOCATION, or, made before, in URL alone).
    /// </summary>
    private static string Event(string token, bool record, string? location = null, bool linkInLocation = true)
    {
        var link = TalkAdapterHarness.Public + "call/" + token;
        var place = linkInLocation ? (location is null ? link : location + "\\, " + link) : location;
        return string.Join("\r\n",
            new[]
            {
                "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", "BEGIN:VEVENT", "UID:meeting-uid", "DTSTAMP:20261123T000000Z",
                "DTSTART:20261201T090000Z", "DTEND:20261201T100000Z", "SUMMARY:Q3 review", "SEQUENCE:0",
                place is null ? null : "LOCATION:" + place,
                "URL:" + link,
                record ? "X-NIGHTWARE-ONLINE-MEETING:" + token : null,
                record ? null : "ORGANIZER;CN=Alice Anderson:mailto:alice@example.com",
                record ? null : "ATTENDEE;CN=Bob Brown;PARTSTAT=NEEDS-ACTION:mailto:bob@example.com",
                "END:VEVENT", "END:VCALENDAR", string.Empty,
            }.OfType<string>());
    }

    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
}

using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using DomainEvent = Nightware.Domain.Calendar.CalendarEvent;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

public sealed class IcalNetCalendarCodecTests
{
    private const string Href = "calendars/alice/personal/weekly.ics";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
    private readonly IcalNetCalendarCodec _codec = new(new FakeTimeProvider(Now));

    private static string Ics(string name) => Fixture.Read(Path.Combine("ICal", name));

    private CalendarDocument Parse(string name, CalendarParseOptions? options = null) =>
        _codec.Parse(Ics(name), options ?? CalendarParseOptions.Default).Value;

    private CalendarDocument RoundTrip(CalendarDocument document) => _codec.Parse(_codec.Serialize(document)).Value;

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    // ---- recurring weekly with an exception and an EXDATE -------------------------------------

    [Fact]
    public void Weekly_series_master_maps_every_field()
    {
        var document = Parse("weekly-exception-exdate.ics", new CalendarParseOptions { Href = Href, Mailbox = new MailboxRef("BOB@example.com") });

        document.Method.ShouldBeNull();
        document.Events.Count.ShouldBe(3);
        var master = document.Events.Single(e => e.Type == EventType.SeriesMaster);
        master.Id.ShouldBe(DavItemId.Resource(Href).ToEventId());
        master.ICalUId.ShouldBe("5c1b2a7e-3f4d-4e0a-9d8b-weekly");
        master.Subject.ShouldBe("Team standup");
        master.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 2, 9, 30, 0), "Europe/Berlin"));
        master.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 2, 10, 0, 0), "Europe/Berlin"));
        master.IsAllDay.ShouldBeFalse();
        master.Sequence.ShouldBe(2);
        master.Location!.DisplayName.ShouldBe("Room 4.12");
        master.Location.LocationEmailAddress.ShouldBeNull();
        master.BodyHtml.ShouldBe("Daily-ish sync, bring updates.<br>Agenda in the wiki.");
        master.BodyPreview.ShouldBe("Daily-ish sync, bring updates. Agenda in the wiki.");
        master.Organizer.ShouldBe(new EmailAddress("alice@example.com", "Alice Example"));
        master.IsOrganizer.ShouldBeFalse();
        master.ResponseStatus.Response.ShouldBe(ResponseType.TentativelyAccepted);
        master.Attendees.Select(a => (a.EmailAddress.Address, a.Type, a.Status.Response)).ShouldBe(
        [
            ("alice@example.com", AttendeeType.Required, ResponseType.Accepted),
            ("bob@example.com", AttendeeType.Required, ResponseType.TentativelyAccepted),
            ("carol@example.com", AttendeeType.Optional, ResponseType.NotResponded),
        ]);
        master.ShowAs.ShouldBe(ShowAs.Busy);
        master.IsReminderOn.ShouldBeTrue();
        master.ReminderMinutesBeforeStart.ShouldBe(10);
        master.Categories.ShouldBe(["Work", "Standup"]);
        master.Sensitivity.ShouldBe("normal");
        master.CreatedDateTime.ShouldBe(Utc(2026, 11, 1, 8, 0));
        master.LastModifiedDateTime.ShouldBe(Utc(2026, 11, 2, 9, 0));
        master.IsCancelled.ShouldBeFalse();
        master.IsOnlineMeeting.ShouldBeFalse();

        var recurrence = master.Recurrence.ShouldNotBeNull();
        recurrence.Pattern.Type.ShouldBe(RecurrencePatternType.Weekly);
        recurrence.Pattern.Interval.ShouldBe(1);
        recurrence.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Monday, DayOfWeek.Wednesday]);
        recurrence.Pattern.FirstDayOfWeek.ShouldBe(DayOfWeek.Monday);
        recurrence.Range.Type.ShouldBe(RecurrenceRangeType.EndDate);
        recurrence.Range.StartDate.ShouldBe(new DateOnly(2026, 11, 2));
        recurrence.Range.EndDate.ShouldBe(new DateOnly(2026, 12, 30)); // UNTIL 22:59:59Z is 23:59:59 in Berlin
        recurrence.Range.RecurrenceTimeZone.ShouldBe("Europe/Berlin");
    }

    [Fact]
    public void Exception_instance_has_occurrence_id_and_original_start()
    {
        var document = Parse("weekly-exception-exdate.ics", new CalendarParseOptions { Href = Href });
        var exception = document.Events.Single(e => e.Type == EventType.Exception && !e.IsCancelled);

        exception.Subject.ShouldBe("Team standup (moved)");
        exception.OriginalStart.ShouldBe(Utc(2026, 11, 4, 8, 30));
        exception.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 4, 11, 0, 0), "Europe/Berlin"));
        exception.SeriesMasterId.ShouldBe(DavItemId.Resource(Href).ToEventId());
        exception.Id.ShouldBe(DavItemId.Occurrence(Href, DavRecurrenceId.FromUtc(Utc(2026, 11, 4, 8, 30))).ToEventId());
        exception.Recurrence.ShouldBeNull();
    }

    [Fact]
    public void Exdate_is_a_cancelled_exception_instance()
    {
        var document = Parse("weekly-exception-exdate.ics", new CalendarParseOptions { Href = Href });
        var excluded = document.Events.Single(e => e.IsCancelled);

        excluded.Type.ShouldBe(EventType.Exception);
        excluded.OriginalStart.ShouldBe(Utc(2026, 11, 11, 8, 30));
        excluded.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 11, 9, 30, 0), "Europe/Berlin"));
        excluded.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 11, 10, 0, 0), "Europe/Berlin"));
        excluded.Id.ShouldBe(DavItemId.Occurrence(Href, DavRecurrenceId.FromUtc(Utc(2026, 11, 11, 8, 30))).ToEventId());
        excluded.Recurrence.ShouldBeNull();
        excluded.Subject.ShouldBe("Team standup");
    }

    [Fact]
    public void Weekly_series_round_trips_with_exdate_exception_and_vtimezone()
    {
        var original = Parse("weekly-exception-exdate.ics");
        var ics = _codec.Serialize(original);

        ics.ShouldContain("PRODID:-//Nightware//Nightware//EN");
        ics.ShouldContain("TZID:Europe/Berlin");
        ics.ShouldContain("BEGIN:VTIMEZONE");
        ics.ShouldContain("DTSTART;TZID=Europe/Berlin:20261102T093000");
        ics.ShouldContain("EXDATE;TZID=Europe/Berlin:20261111T093000");
        ics.ShouldContain("RECURRENCE-ID;TZID=Europe/Berlin:20261104T093000");
        ics.ShouldContain("UNTIL=20261230T083000Z"); // the last occurrence's start in UTC
        ics.ShouldContain("DTSTAMP:20261115T080000Z");

        var reparsed = _codec.Parse(ics).Value;
        reparsed.Events.Count.ShouldBe(3);
        var master = reparsed.Events.Single(e => e.Type == EventType.SeriesMaster);
        var before = original.Events.Single(e => e.Type == EventType.SeriesMaster);
        master.Recurrence!.Range.ShouldBe(before.Recurrence!.Range);
        master.Recurrence.Pattern.Type.ShouldBe(before.Recurrence.Pattern.Type);
        master.Recurrence.Pattern.DaysOfWeek.ShouldBe(before.Recurrence.Pattern.DaysOfWeek);
        master.Start.ShouldBe(before.Start);
        master.Attendees.ShouldBe(before.Attendees);
        master.Organizer.ShouldBe(before.Organizer);
        master.Categories.ShouldBe(before.Categories);
        master.ReminderMinutesBeforeStart.ShouldBe(10);
        reparsed.Events.Single(e => e.IsCancelled).OriginalStart.ShouldBe(Utc(2026, 11, 11, 8, 30));
        reparsed.Events.Single(e => e.Type == EventType.Exception && !e.IsCancelled).OriginalStart.ShouldBe(Utc(2026, 11, 4, 8, 30));
    }

    // ---- all-day ----------------------------------------------------------------------------------

    [Fact]
    public void All_day_events_are_midnight_utc_dates()
    {
        var document = Parse("all-day.ics", new CalendarParseOptions { Href = "calendars/alice/personal/holiday.ics" });
        var master = document.Events.Single(e => e.Type == EventType.SeriesMaster);

        master.IsAllDay.ShouldBeTrue();
        master.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 12, 25), "UTC"));
        master.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 12, 27), "UTC"));
        master.ShowAs.ShouldBe(ShowAs.Free);
        var recurrence = master.Recurrence.ShouldNotBeNull();
        recurrence.Pattern.Type.ShouldBe(RecurrencePatternType.AbsoluteYearly);
        recurrence.Pattern.Month.ShouldBe(12);
        recurrence.Pattern.DayOfMonth.ShouldBe(25);
        recurrence.Range.EndDate.ShouldBe(new DateOnly(2028, 12, 25));
        recurrence.Range.RecurrenceTimeZone.ShouldBeNull();

        var excluded = document.Events.Single(e => e.IsCancelled);
        excluded.OriginalStart.ShouldBe(Utc(2027, 12, 25, 0, 0));
        excluded.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2027, 12, 25), "UTC"));
        DavItemId.TryParse(excluded.Id.Value, out var id).ShouldBeTrue();
        id!.Value.RecurrenceId!.Value.Value.ShouldBe("20271225");
    }

    [Fact]
    public void All_day_round_trip_uses_date_values()
    {
        var ics = _codec.Serialize(Parse("all-day.ics"));
        ics.ShouldContain("DTSTART;VALUE=DATE:20261225");
        ics.ShouldContain("DTEND;VALUE=DATE:20261227");
        ics.ShouldContain("UNTIL=20281225");
        ics.ShouldContain("EXDATE;VALUE=DATE:20271225");
        ics.ShouldContain("TRANSP:TRANSPARENT");
        ics.ShouldNotContain("BEGIN:VTIMEZONE");
        var reparsed = _codec.Parse(ics).Value;
        reparsed.Events.Single(e => e.Type == EventType.SeriesMaster).IsAllDay.ShouldBeTrue();
        reparsed.Events.Single(e => e.IsCancelled).OriginalStart.ShouldBe(Utc(2027, 12, 25, 0, 0));
    }

    [Fact]
    public void All_day_with_end_not_after_start_gets_one_day()
    {
        var ev = new DomainEvent(default, "Day", new DateTimeTimeZone(new DateTime(2026, 5, 1), "UTC"), new DateTimeTimeZone(new DateTime(2026, 5, 1), "UTC")) { IsAllDay = true };
        _codec.Serialize(new CalendarDocument(null, [ev])).ShouldContain("DTEND;VALUE=DATE:20260502");
    }

    // ---- floating vs TZID vs UTC --------------------------------------------------------------

    [Fact]
    public void Floating_times_use_the_configured_zone()
    {
        Parse("time-forms.ics").Events.Single(e => e.ICalUId == "floating").Start
            .ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 16, 9, 0, 0), "UTC"));
        Parse("time-forms.ics", new CalendarParseOptions { FloatingTimeZone = "Australia/Sydney" }).Events.Single(e => e.ICalUId == "floating").Start
            .ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 16, 9, 0, 0), "Australia/Sydney"));
    }

    [Fact]
    public void Utc_times_and_duration()
    {
        var ev = Parse("time-forms.ics").Events.Single(e => e.ICalUId == "utc");
        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 16, 9, 0, 0), "UTC"));
        ev.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 16, 9, 45, 0), "UTC"));
    }

    [Fact]
    public void Vendor_prefixed_tzids_resolve_to_iana()
    {
        var ev = Parse("time-forms.ics").Events.Single(e => e.ICalUId == "zoned");
        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 16, 9, 0, 0), "America/New_York"));
        _codec.Serialize(new CalendarDocument(null, [ev])).ShouldContain("DTSTART;TZID=America/New_York:20261116T090000");
    }

    // ---- DST boundary -----------------------------------------------------------------------------

    [Fact]
    public void Recurrence_ids_across_a_dst_change_use_the_right_offset()
    {
        var document = Parse("dst-sydney.ics", new CalendarParseOptions { Href = "calendars/alice/personal/dst.ics" });
        var master = document.Events.Single(e => e.Type == EventType.SeriesMaster);
        var exception = document.Events.Single(e => e.Type == EventType.Exception);

        master.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 3, 30, 9, 0, 0), "Australia/Sydney"));
        master.Recurrence!.Range.Type.ShouldBe(RecurrenceRangeType.Numbered);
        master.Recurrence.Range.NumberOfOccurrences.ShouldBe(3);
        exception.OriginalStart.ShouldBe(Utc(2026, 4, 5, 23, 0)); // 09:00 AEST (+10), not AEDT
        DavItemId.TryParse(exception.Id.Value, out var id).ShouldBeTrue();
        id!.Value.RecurrenceId!.Value.Value.ShouldBe("20260405T230000Z");

        var ics = _codec.Serialize(document);
        ics.ShouldContain("RECURRENCE-ID;TZID=Australia/Sydney:20260406T090000");
        ics.ShouldContain("COUNT=3");
    }

    [Fact]
    public void Vtimezone_observances_give_the_offset_on_each_side_of_dst()
    {
        var calendar = IcalNetCalendarCodec.Load(Ics("outlook-display-name-tzid.ics")).Value;
        var zone = calendar.TimeZones.Single();
        IcalZoneContext.OffsetFromObservances(zone, new DateTime(2026, 1, 15, 12, 0, 0)).ShouldBe(TimeSpan.FromHours(11));
        IcalZoneContext.OffsetFromObservances(zone, new DateTime(2026, 7, 15, 12, 0, 0)).ShouldBe(TimeSpan.FromHours(10));
        IcalZoneContext.OffsetFromObservances(zone, new DateTime(2026, 10, 4, 3, 0, 0)).ShouldBe(TimeSpan.FromHours(11));
    }

    // ---- Outlook / Google invitations -------------------------------------------------------------

    [Fact]
    public void Outlook_request_with_windows_tzid()
    {
        var document = Parse("outlook-request.ics", new CalendarParseOptions { Mailbox = new MailboxRef("alice@example.com") });
        document.Method.ShouldBe("REQUEST");
        var ev = document.Events.ShouldHaveSingleItem();

        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 12, 8, 14, 0, 0), "AUS Eastern Standard Time"));
        ev.End.DateTime.ShouldBe(new DateTime(2026, 12, 8, 15, 0, 0));
        ev.Type.ShouldBe(EventType.SeriesMaster);
        ev.ShowAs.ShouldBe(ShowAs.Tentative);
        ev.IsOnlineMeeting.ShouldBeTrue();
        ev.OnlineMeetingUrl.ShouldBe("https://teams.microsoft.com/l/meetup-join/abc");
        ev.BodyHtml.ShouldStartWith("<html><body><p>Quarterly review.</p>");
        ev.BodyPreview.ShouldStartWith("Quarterly review. __________________ Microsoft Teams meeting");
        ev.Location!.DisplayName.ShouldBe("Level 3 Boardroom");
        ev.Location.LocationEmailAddress.ShouldBe("boardroom.l3@contoso.com");
        ev.Locations.ShouldHaveSingleItem();
        ev.Attendees.Single(a => a.EmailAddress.Address == "boardroom.l3@contoso.com").Type.ShouldBe(AttendeeType.Resource);
        ev.Attendees.Single(a => a.EmailAddress.Address == "bob@example.com").Type.ShouldBe(AttendeeType.Optional);
        ev.ResponseStatus.Response.ShouldBe(ResponseType.NotResponded);
        ev.IsOrganizer.ShouldBeFalse();
        ev.ReminderMinutesBeforeStart.ShouldBe(15);
        var recurrence = ev.Recurrence.ShouldNotBeNull();
        recurrence.Pattern.Type.ShouldBe(RecurrencePatternType.RelativeMonthly);
        recurrence.Pattern.Interval.ShouldBe(3);
        recurrence.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Tuesday]);
        recurrence.Pattern.Index.ShouldBe("second");
        recurrence.Range.NumberOfOccurrences.ShouldBe(4);
        recurrence.Range.RecurrenceTimeZone.ShouldBe("AUS Eastern Standard Time");

        var ics = _codec.Serialize(document);
        ics.ShouldContain("METHOD:REQUEST");
        ics.ShouldContain("DTSTART;TZID=Australia/Sydney:20261208T140000");
        ics.ShouldContain("TZID:Australia/Sydney");
        ics.ShouldContain("X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE");
        ics.ShouldContain("CUTYPE=ROOM");
        var reparsed = _codec.Parse(ics).Value.Events.Single();
        reparsed.Recurrence!.Pattern.ShouldBe(recurrence.Pattern with { DaysOfWeek = reparsed.Recurrence.Pattern.DaysOfWeek });
        reparsed.Recurrence.Pattern.DaysOfWeek.ShouldBe(recurrence.Pattern.DaysOfWeek);
        reparsed.ShowAs.ShouldBe(ShowAs.Tentative);
        reparsed.BodyHtml.ShouldBe(ev.BodyHtml);
        reparsed.Location!.LocationEmailAddress.ShouldBe("boardroom.l3@contoso.com");
    }

    [Fact]
    public void Outlook_display_name_tzid_falls_back_to_the_vtimezone_offsets()
    {
        var ev = Parse("outlook-display-name-tzid.ics").Events.Single();
        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 7, 10, 0, 0, 0), "UTC")); // 10:00 AEST
        ev.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 7, 10, 0, 30, 0), "UTC"));
        ev.Organizer!.Name.ShouldBe("Dana Manager");
    }

    [Fact]
    public void Google_request_with_last_friday_rule_and_meet_link()
    {
        var document = Parse("google-request.ics", new CalendarParseOptions { Mailbox = new MailboxRef("alice@example.com") });
        var ev = document.Events.Single();

        ev.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 17, 13, 0, 0), "America/New_York"));
        ev.Recurrence!.Pattern.Type.ShouldBe(RecurrencePatternType.RelativeMonthly);
        ev.Recurrence.Pattern.Index.ShouldBe("last");
        ev.Recurrence.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Friday]);
        ev.Recurrence.Range.Type.ShouldBe(RecurrenceRangeType.NoEnd);
        ev.OnlineMeetingUrl.ShouldBe("https://meet.google.com/abc-defg-hij");
        ev.Location.ShouldBeNull();
        ev.ResponseStatus.Response.ShouldBe(ResponseType.NotResponded);
        ev.Organizer!.Address.ShouldBe("erin@gmail.com");
        ev.ReminderMinutesBeforeStart.ShouldBe(30);
        ev.BodyPreview.ShouldBe("Last-Friday partner sync. Join with Google Meet: https://meet.google.com/abc-defg-hij");

        var reparsed = RoundTrip(document).Events.Single();
        reparsed.Recurrence!.Pattern.Index.ShouldBe("last");
        reparsed.Recurrence.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Friday]);
    }

    // ---- iTIP methods ----------------------------------------------------------------------------

    [Fact]
    public void Cancel_of_one_instance()
    {
        var document = Parse("cancel.ics");
        document.Method.ShouldBe("CANCEL");
        var ev = document.Events.Single();
        ev.IsCancelled.ShouldBeTrue();
        ev.Type.ShouldBe(EventType.Exception);
        ev.OriginalStart.ShouldBe(Utc(2026, 11, 18, 8, 30));
        ev.Sequence.ShouldBe(4);

        var ics = _codec.Serialize(document);
        ics.ShouldContain("METHOD:CANCEL");
        ics.ShouldContain("STATUS:CANCELLED");
        ics.ShouldContain("RECURRENCE-ID:20261118T083000Z");
        ics.ShouldNotContain("EXDATE");
    }

    [Fact]
    public void Reply_with_declined_attendee_and_no_dtend()
    {
        var document = Parse("reply.ics", new CalendarParseOptions { Mailbox = new MailboxRef("bob@example.com") });
        document.Method.ShouldBe("REPLY");
        var ev = document.Events.Single();
        ev.Attendees.Single().Status.Response.ShouldBe(ResponseType.Declined);
        ev.ResponseStatus.Response.ShouldBe(ResponseType.Declined);
        ev.End.ShouldBe(ev.Start);
        RoundTrip(document).Method.ShouldBe("REPLY");
    }

    [Fact]
    public void Cancelled_status_marks_the_event_cancelled()
    {
        var ics = Ics("reply.ics").Replace("METHOD:REPLY", "METHOD:PUBLISH", StringComparison.Ordinal).Replace("REQUEST-STATUS:2.0;Success", "STATUS:CANCELLED", StringComparison.Ordinal);
        _codec.Parse(ics).Value.Events.Single().IsCancelled.ShouldBeTrue();
    }

    // ---- expanded calendar-query output -----------------------------------------------------------

    [Fact]
    public void Expanded_instances_are_occurrences_with_occurrence_ids()
    {
        var multistatus = DavXml.ParseMultistatus(Fixture.Read("calendar-query-expanded.xml"));
        var data = multistatus.Responses[0].CalendarData!;
        var events = _codec.Parse(data, new CalendarParseOptions { Href = "calendars/alice/personal/7F1C2E9A-weekly.ics", Expanded = true }).Value.Events;

        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.Type == EventType.Occurrence);
        events[1].OriginalStart.ShouldBe(Utc(2026, 11, 18, 23, 0));
        events[1].Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 19, 0, 0, 0), "UTC"));
        DavItemId.TryParse(events[1].Id.Value, out var id).ShouldBeTrue();
        id!.Value.RecurrenceId!.Value.Value.ShouldBe("20261118T230000Z");
        id.Value.Href.ShouldBe("calendars/alice/personal/7F1C2E9A-weekly.ics");
    }

    // ---- serialising new events -------------------------------------------------------------------

    [Fact]
    public void Serialises_a_new_meeting_with_windows_zone_rooms_and_reminder()
    {
        var ev = new DomainEvent(default, "Planning; Q1, draft", new DateTimeTimeZone(new DateTime(2027, 1, 12, 10, 0, 0), "W. Europe Standard Time"), new DateTimeTimeZone(new DateTime(2027, 1, 12, 11, 0, 0), "W. Europe Standard Time"))
        {
            ICalUId = "new-1",
            Organizer = new EmailAddress("alice@example.com", "Alice Example"),
            Attendees =
            [
                new Attendee(new EmailAddress("bob@example.com", "Bob"), AttendeeType.Required, new ResponseStatus(ResponseType.None)),
                new Attendee(new EmailAddress("carol@example.com"), AttendeeType.Optional, new ResponseStatus(ResponseType.Accepted)),
                new Attendee(new EmailAddress("boardroom@example.com", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.None)),
                new Attendee(new EmailAddress("projector@example.com", "Projector"), AttendeeType.Resource, new ResponseStatus(ResponseType.None)),
            ],
            Location = new Location("Boardroom") { LocationEmailAddress = "boardroom@example.com" },
            BodyHtml = "<p>Agenda:</p><ul><li>One</li></ul>",
            ShowAs = ShowAs.Oof,
            IsReminderOn = true,
            ReminderMinutesBeforeStart = 15,
            Sensitivity = "private",
            Categories = ["Planning"],
            OnlineMeetingUrl = "https://meet.example.com/xyz",
            IsOnlineMeeting = true,
            Sequence = 1,
        };

        var ics = _codec.Serialize(new CalendarDocument("REQUEST", [ev]));

        ics.ShouldContain("UID:new-1");
        ics.ShouldContain("DTSTART;TZID=Europe/Berlin:20270112T100000");
        ics.ShouldContain("BEGIN:VTIMEZONE");
        ics.ShouldContain(@"SUMMARY:Planning\; Q1\, draft");
        ics.ShouldContain("X-MICROSOFT-CDO-BUSYSTATUS:OOF");
        ics.ShouldContain("TRIGGER:-PT15M");
        ics.ShouldContain("CLASS:PRIVATE");
        ics.ShouldContain("URL:https://meet.example.com/xyz");
        ics.ShouldContain("DTSTAMP:20261115T080000Z");
        ics.ShouldContain("SEQUENCE:1");
        Unfolded(ics).ShouldContain("CUTYPE=ROOM");
        Unfolded(ics).ShouldContain("CUTYPE=RESOURCE");
        Unfolded(ics).ShouldContain("RSVP=TRUE");
        Unfolded(ics).ShouldContain("X-ALT-DESC;FMTTYPE=text/html:<p>Agenda:</p><ul><li>One</li></ul>");

        var back = _codec.Parse(ics, new CalendarParseOptions { Mailbox = new MailboxRef("alice@example.com") }).Value.Events.Single();
        back.Subject.ShouldBe("Planning; Q1, draft");
        back.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2027, 1, 12, 10, 0, 0), "Europe/Berlin"));
        back.IsOrganizer.ShouldBeTrue();
        back.ResponseStatus.Response.ShouldBe(ResponseType.Organizer);
        back.ShowAs.ShouldBe(ShowAs.Oof);
        back.Sensitivity.ShouldBe("private");
        back.Attendees.Select(a => a.Type).ShouldBe([AttendeeType.Required, AttendeeType.Optional, AttendeeType.Resource, AttendeeType.Resource]);
        back.Attendees[1].Status.Response.ShouldBe(ResponseType.Accepted);
        back.Location!.LocationEmailAddress.ShouldBe("boardroom@example.com");
        back.Locations.Count.ShouldBe(2);
        back.OnlineMeetingUrl.ShouldBe("https://meet.example.com/xyz");
        back.BodyPreview.ShouldBe("Agenda: One");
        back.Categories.ShouldBe(["Planning"]);
    }

    [Fact]
    public void Unknown_zones_are_written_in_utc()
    {
        var ev = new DomainEvent(default, "x", new DateTimeTimeZone(new DateTime(2027, 1, 12, 10, 0, 0), "Mars/Olympus_Mons"), new DateTimeTimeZone(new DateTime(2027, 1, 12, 11, 0, 0), "UTC"));
        var ics = _codec.Serialize(new CalendarDocument(null, [ev]));
        ics.ShouldContain("DTSTART:20270112T100000Z");
        ics.ShouldNotContain("BEGIN:VTIMEZONE");
    }

    public static TheoryData<PatternedRecurrence> Patterns()
    {
        var start = new DateOnly(2027, 1, 4);
        return new TheoryData<PatternedRecurrence>
        {
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 2), new RecurrenceRange(RecurrenceRangeType.Numbered, start) { NumberOfOccurrences = 10, RecurrenceTimeZone = "Europe/Berlin" }),
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Friday], FirstDayOfWeek = DayOfWeek.Sunday }, new RecurrenceRange(RecurrenceRangeType.NoEnd, start) { RecurrenceTimeZone = "Europe/Berlin" }),
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.AbsoluteMonthly, 1) { DayOfMonth = 4, FirstDayOfWeek = DayOfWeek.Monday }, new RecurrenceRange(RecurrenceRangeType.EndDate, start) { EndDate = new DateOnly(2027, 12, 4), RecurrenceTimeZone = "Europe/Berlin" }),
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.RelativeMonthly, 1) { DaysOfWeek = [DayOfWeek.Monday], Index = "first", FirstDayOfWeek = DayOfWeek.Monday }, new RecurrenceRange(RecurrenceRangeType.NoEnd, start) { RecurrenceTimeZone = "Europe/Berlin" }),
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.AbsoluteYearly, 1) { DayOfMonth = 4, Month = 1, FirstDayOfWeek = DayOfWeek.Monday }, new RecurrenceRange(RecurrenceRangeType.Numbered, start) { NumberOfOccurrences = 5, RecurrenceTimeZone = "Europe/Berlin" }),
            new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.RelativeYearly, 1) { DaysOfWeek = [DayOfWeek.Monday], Index = "first", Month = 1, FirstDayOfWeek = DayOfWeek.Monday }, new RecurrenceRange(RecurrenceRangeType.NoEnd, start) { RecurrenceTimeZone = "Europe/Berlin" }),
        };
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Recurrence_patterns_round_trip(PatternedRecurrence recurrence)
    {
        var ev = new DomainEvent(default, "Series", new DateTimeTimeZone(new DateTime(2027, 1, 4, 9, 0, 0), "Europe/Berlin"), new DateTimeTimeZone(new DateTime(2027, 1, 4, 10, 0, 0), "Europe/Berlin"))
        {
            ICalUId = "series",
            Type = EventType.SeriesMaster,
            Recurrence = recurrence,
        };
        var back = RoundTrip(new CalendarDocument(null, [ev])).Events.Single();
        back.Type.ShouldBe(EventType.SeriesMaster);
        back.Recurrence!.Range.ShouldBe(recurrence.Range);
        back.Recurrence.Pattern.Type.ShouldBe(recurrence.Pattern.Type);
        back.Recurrence.Pattern.Interval.ShouldBe(recurrence.Pattern.Interval);
        back.Recurrence.Pattern.DaysOfWeek.ShouldBe(recurrence.Pattern.DaysOfWeek);
        back.Recurrence.Pattern.DayOfMonth.ShouldBe(recurrence.Pattern.DayOfMonth);
        back.Recurrence.Pattern.Month.ShouldBe(recurrence.Pattern.Type is RecurrencePatternType.AbsoluteYearly or RecurrencePatternType.RelativeYearly ? recurrence.Pattern.Month : 0);
        back.Recurrence.Pattern.Index.ShouldBe(recurrence.Pattern.Index);
        back.Recurrence.Pattern.FirstDayOfWeek.ShouldBe(recurrence.Pattern.Type == RecurrencePatternType.Weekly ? recurrence.Pattern.FirstDayOfWeek : DayOfWeek.Monday);
    }

    [Theory]
    [InlineData("FREQ=HOURLY;INTERVAL=2")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO;BYHOUR=9,15")]
    [InlineData("FREQ=YEARLY;BYWEEKNO=20")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=-1")]
    [InlineData("FREQ=MONTHLY;BYDAY=TU;BYSETPOS=3,4")]
    [InlineData("FREQ=MONTHLY;BYDAY=5TU")]
    [InlineData("FREQ=YEARLY;BYMONTH=1,7")]
    public void Unmappable_rules_leave_recurrence_null(string rule)
    {
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:u\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260105T090000Z\r\nRRULE:{rule}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var ev = _codec.Parse(ics).Value.Events.Single();
        ev.Type.ShouldBe(EventType.SeriesMaster);
        ev.Recurrence.ShouldBeNull();
    }

    [Theory]
    [InlineData("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", RecurrencePatternType.Weekly)]
    [InlineData("FREQ=WEEKLY", RecurrencePatternType.Weekly)]
    [InlineData("FREQ=MONTHLY", RecurrencePatternType.AbsoluteMonthly)]
    [InlineData("FREQ=MONTHLY;BYDAY=1MO", RecurrencePatternType.RelativeMonthly)]
    [InlineData("FREQ=YEARLY", RecurrencePatternType.AbsoluteYearly)]
    [InlineData("FREQ=YEARLY;BYMONTH=1;BYDAY=1MO", RecurrencePatternType.RelativeYearly)]
    public void Implicit_parts_come_from_dtstart(string rule, RecurrencePatternType type)
    {
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:u\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260105T090000Z\r\nRRULE:{rule}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var pattern = _codec.Parse(ics).Value.Events.Single().Recurrence!.Pattern;
        pattern.Type.ShouldBe(type);
        if (type == RecurrencePatternType.Weekly && !rule.Contains("BYDAY", StringComparison.Ordinal))
        {
            pattern.DaysOfWeek.ShouldBe([DayOfWeek.Monday]);
        }

        if (type is RecurrencePatternType.AbsoluteMonthly or RecurrencePatternType.AbsoluteYearly)
        {
            pattern.DayOfMonth.ShouldBe(5);
        }
    }

    [Fact]
    public void Absolute_and_end_related_alarm_triggers()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260105T090000Z\r\nDTEND:20260105T100000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;VALUE=DATE-TIME:20260105T083000Z\r\nEND:VALARM\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:b\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260105T090000Z\r\nDTEND:20260105T100000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER;RELATED=END:-PT90M\r\nEND:VALARM\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var events = _codec.Parse(ics).Value.Events;
        events[0].ReminderMinutesBeforeStart.ShouldBe(30);
        events[1].ReminderMinutesBeforeStart.ShouldBe(30);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:notadate\r\n")]
    public void Invalid_input_is_a_validation_error(string ics) =>
        _codec.Parse(ics).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public void Unsafe_hrefs_are_rejected() =>
        _codec.Parse(Ics("reply.ics"), new CalendarParseOptions { Href = "../x.ics" }).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public void Plain_parse_leaves_placeholder_ids() =>
        _codec.Parse(Ics("reply.ics")).Value.Events.Single().Id.Value.ShouldBeEmpty();

    private static string Unfolded(string ics) => ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
}

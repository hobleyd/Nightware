using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

/// <summary>Graph PATCH and forward applied in place to the stored iCalendar.</summary>
public sealed class IcsEditorPatchTests
{
    private const string Alice = "alice@example.com";
    private static readonly DavRecurrenceId UnmodifiedMonday = DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 9, 8, 30, 0, TimeSpan.Zero));

    private readonly IcsEditor _editor = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
    private readonly IcalNetCalendarCodec _codec = new(TimeProvider.System);

    private static string Weekly => Fixture.Read(Path.Combine("ICal", "weekly-exception-exdate.ics"));

    private static string Unfolded(string ics) => ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    private static Attendee Required(string address, string? name = null) =>
        new(new EmailAddress(address, name), AttendeeType.Required, new ResponseStatus(ResponseType.None));

    private IReadOnlyList<CalendarEvent> Parse(string ics) => _codec.Parse(ics).Value.Events;

    private CalendarEvent Master(string ics) => Parse(ics).Single(e => e.Type == EventType.SeriesMaster);

    [Fact]
    public void The_organizer_moving_the_series_bumps_every_sequence_and_keeps_the_rest()
    {
        var patch = new EventPatch
        {
            Start = new DateTimeTimeZone(new DateTime(2026, 11, 2, 10, 0, 0), "Europe/Berlin"),
            End = new DateTimeTimeZone(new DateTime(2026, 11, 2, 10, 45, 0), "Europe/Berlin"),
        };

        var edited = Unfolded(_editor.ApplyPatch(Weekly, patch, null, Alice).Value);

        var master = Master(edited);
        master.Start.ShouldBe(patch.Start);
        master.End.ShouldBe(patch.End);
        master.Sequence.ShouldBe(3);
        Parse(edited).Single(e => e.Subject == "Team standup (moved)").Sequence.ShouldBe(4);
        master.Attendees.Single(a => a.EmailAddress.Address == "bob@example.com").Status.Response.ShouldBe(ResponseType.TentativelyAccepted);
        master.Categories.ShouldBe(["Work", "Standup"]);
        edited.ShouldContain("LAST-MODIFIED:20261120T120000Z");
    }

    [Fact]
    public void Resending_the_stored_times_in_another_zone_changes_nothing()
    {
        // NightMail's full edit resends start/end in the device's zone: 09:30 Berlin is 19:30 Sydney.
        var patch = new EventPatch
        {
            Subject = "Team standup",
            Start = new DateTimeTimeZone(new DateTime(2026, 11, 2, 19, 30, 0), "Australia/Sydney"),
            End = new DateTimeTimeZone(new DateTime(2026, 11, 2, 20, 0, 0), "Australia/Sydney"),
        };

        var edited = _editor.ApplyPatch(Weekly, patch, null, Alice).Value;

        edited.ShouldContain("DTSTART;TZID=Europe/Berlin:20261102T093000");
        Master(edited).Sequence.ShouldBe(2);
    }

    [Fact]
    public void An_attendee_editing_their_copy_does_not_bump_the_sequence()
    {
        var patch = new EventPatch { Start = new DateTimeTimeZone(new DateTime(2026, 11, 2, 10, 0, 0), "Europe/Berlin") };

        var master = Master(_editor.ApplyPatch(Weekly, patch, null, "bob@example.com").Value);

        master.Sequence.ShouldBe(2);
        master.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 2, 10, 30, 0), "Europe/Berlin")); // duration kept
    }

    [Fact]
    public void Attendees_merge_by_address()
    {
        var patch = new EventPatch { Attendees = [Required("BOB@example.com"), Required("dave@example.com", "Dave")] };

        var edited = Unfolded(_editor.ApplyPatch(Weekly, patch, null, Alice).Value);

        var attendees = Master(edited).Attendees;
        attendees.Select(a => a.EmailAddress.Address).ShouldBe(["alice@example.com", "bob@example.com", "dave@example.com"]);
        attendees[1].Status.Response.ShouldBe(ResponseType.TentativelyAccepted);
        attendees[1].EmailAddress.Name.ShouldBe("Bob Builder");
        attendees[2].Status.Response.ShouldBe(ResponseType.NotResponded);
        edited.ShouldContain("ATTENDEE;CN=Dave;ROLE=REQ-PARTICIPANT;CUTYPE=INDIVIDUAL;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:dave@example.com");
        edited.ShouldContain("ROLE=CHAIR"); // the organizer's own line survives
        var moved = Parse(edited).Single(e => e.Subject == "Team standup (moved)");
        moved.Attendees.Select(a => a.EmailAddress.Address).ShouldBe(["bob@example.com", "dave@example.com"]);
        moved.Attendees[0].Status.Response.ShouldBe(ResponseType.Accepted);
    }

    [Fact]
    public void Rooms_named_by_a_location_address_are_room_attendees()
    {
        var room = new Attendee(new EmailAddress("boardroom@example.com", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.None));
        var patch = new EventPatch
        {
            Attendees = [Required("bob@example.com"), room],
            Location = new Location("Boardroom") { LocationEmailAddress = "boardroom@example.com" },
        };

        var edited = Unfolded(_editor.ApplyPatch(Weekly, patch, null, Alice).Value);

        edited.ShouldContain("CUTYPE=ROOM");
        var master = Master(edited);
        master.Location!.LocationEmailAddress.ShouldBe("boardroom@example.com");
        master.Attendees.Single(a => a.EmailAddress.Address == "boardroom@example.com").Type.ShouldBe(AttendeeType.Resource);
    }

    [Fact]
    public void An_appointment_that_gains_attendees_gets_an_organizer()
    {
        var single = _codec.Serialize(new CalendarDocument(null,
        [
            new CalendarEvent(new EventId(string.Empty), "Focus", DateTimeTimeZone.Utc(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero)), DateTimeTimeZone.Utc(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero)))
            {
                ICalUId = "focus-1",
            },
        ]));

        var edited = _editor.ApplyPatch(single, new EventPatch { Attendees = [Required("bob@example.com")] }, null, Alice).Value;

        var ev = _codec.Parse(edited, new CalendarParseOptions { Mailbox = new MailboxRef(Alice) }).Value.Events.Single();
        ev.Organizer!.Address.ShouldBe(Alice);
        ev.IsOrganizer.ShouldBeTrue();
    }

    [Fact]
    public void Details_are_set_and_cleared()
    {
        var patch = new EventPatch
        {
            Subject = "Standup v2",
            BodyHtml = "<p>New <b>agenda</b></p>",
            Location = new Location(string.Empty),
            ShowAs = ShowAs.Free,
            IsReminderOn = false,
            Categories = ["Team"],
        };

        var edited = Unfolded(_editor.ApplyPatch(Weekly, patch, null, Alice).Value);

        var master = Master(edited);
        master.Subject.ShouldBe("Standup v2");
        master.BodyHtml.ShouldBe("<p>New <b>agenda</b></p>");
        master.BodyPreview.ShouldBe("New agenda");
        master.Location.ShouldBeNull();
        master.ShowAs.ShouldBe(ShowAs.Free);
        master.IsReminderOn.ShouldBeFalse();
        master.Categories.ShouldBe(["Team"]);
        master.Sequence.ShouldBe(2);
        edited.ShouldNotContain("Daily-ish sync");
    }

    [Fact]
    public void A_reminder_change_rewrites_the_first_alarm()
    {
        var master = Master(_editor.ApplyPatch(Weekly, new EventPatch { IsReminderOn = true, ReminderMinutesBeforeStart = 30 }, null, Alice).Value);
        master.ReminderMinutesBeforeStart.ShouldBe(30);
    }

    [Fact]
    public void A_new_recurrence_replaces_the_rule_and_reschedules()
    {
        var recurrence = new PatternedRecurrence(
            new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Monday] },
            new RecurrenceRange(RecurrenceRangeType.Numbered, new DateOnly(2026, 11, 2)) { NumberOfOccurrences = 5 });

        var master = Master(_editor.ApplyPatch(Weekly, new EventPatch { Recurrence = recurrence }, null, Alice).Value);

        master.Recurrence!.Pattern.DaysOfWeek.ShouldBe([DayOfWeek.Monday]);
        master.Recurrence.Range.NumberOfOccurrences.ShouldBe(5);
        master.Sequence.ShouldBe(3);
    }

    [Fact]
    public void Patching_an_occurrence_changes_that_occurrence_only()
    {
        var patch = new EventPatch
        {
            Subject = "Standup (late)",
            Start = new DateTimeTimeZone(new DateTime(2026, 11, 9, 11, 0, 0), "Europe/Berlin"),
            End = new DateTimeTimeZone(new DateTime(2026, 11, 9, 11, 30, 0), "Europe/Berlin"),
        };

        var events = Parse(_editor.ApplyPatch(Weekly, patch, UnmodifiedMonday, Alice).Value);

        var occurrence = events.Single(e => e.OriginalStart == UnmodifiedMonday.ToUtc() && !e.IsCancelled);
        occurrence.Subject.ShouldBe("Standup (late)");
        occurrence.Start.ShouldBe(patch.Start);
        occurrence.Sequence.ShouldBe(3);
        var master = events.Single(e => e.Type == EventType.SeriesMaster);
        master.Subject.ShouldBe("Team standup");
        master.Sequence.ShouldBe(2);
    }

    [Fact]
    public void An_occurrence_cannot_take_a_recurrence()
    {
        var recurrence = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 1), new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 11, 9)));
        _editor.ApplyPatch(Weekly, new EventPatch { Recurrence = recurrence }, UnmodifiedMonday, Alice).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Making_an_event_all_day_writes_dates()
    {
        var single = Fixture.Read(Path.Combine("ICal", "reply.ics"));
        var start = Parse(single).Single().Start;

        var edited = _editor.ApplyPatch(single, new EventPatch { IsAllDay = true }, null, Alice).Value;

        var ev = Parse(edited).Single();
        ev.IsAllDay.ShouldBeTrue();
        ev.Start.DateTime.ShouldBe(start.DateTime.Date);
        ev.End.DateTime.ShouldBe(start.DateTime.Date.AddDays(1));
    }

    [Fact]
    public void Forwarding_adds_new_attendees_to_every_component()
    {
        var edited = _editor.AddAttendees(Weekly, [new EmailAddress("pat@example.com"), new EmailAddress("bob@example.com")]).Value;

        var events = Parse(edited).Where(e => !e.IsCancelled).ToList();
        events.ShouldAllBe(e => e.Attendees.Any(a => a.EmailAddress.Address == "pat@example.com" && a.Status.Response == ResponseType.NotResponded));
        events.Single(e => e.Type == EventType.SeriesMaster).Attendees.Count(a => a.EmailAddress.Address == "bob@example.com").ShouldBe(1);
    }

    [Fact]
    public void Forwarding_one_occurrence_invites_to_that_occurrence_only()
    {
        var events = Parse(_editor.AddAttendees(Weekly, [new EmailAddress("pat@example.com")], UnmodifiedMonday).Value);

        events.Single(e => e.OriginalStart == UnmodifiedMonday.ToUtc() && !e.IsCancelled).Attendees.ShouldContain(a => a.EmailAddress.Address == "pat@example.com");
        events.Single(e => e.Type == EventType.SeriesMaster).Attendees.ShouldNotContain(a => a.EmailAddress.Address == "pat@example.com");
    }

    [Fact]
    public void Clearing_the_recurrence_leaves_one_event_at_the_first_occurrence()
    {
        var edited = Unfolded(_editor.ApplyPatch(Weekly, new EventPatch { ClearRecurrence = true }, null, Alice).Value);

        var only = Parse(edited).ShouldHaveSingleItem();
        only.Type.ShouldBe(EventType.SingleInstance);
        only.Recurrence.ShouldBeNull();
        only.Start.ShouldBe(Master(Weekly).Start);
        only.Sequence.ShouldBe(3, "attendees are told: the series is gone");
        var vevent = edited[edited.IndexOf("BEGIN:VEVENT", StringComparison.Ordinal)..];
        vevent.ShouldNotContain("RRULE");
        vevent.ShouldNotContain("EXDATE");
        vevent.ShouldNotContain("RECURRENCE-ID");
    }

    [Fact]
    public void Clearing_the_recurrence_of_a_single_event_changes_no_sequence()
    {
        var single = _editor.ApplyPatch(Weekly, new EventPatch { ClearRecurrence = true }, null, Alice).Value;

        Parse(_editor.ApplyPatch(single, new EventPatch { ClearRecurrence = true }, null, Alice).Value).Single().Sequence.ShouldBe(3);
    }

    [Fact]
    public void An_occurrence_cannot_clear_a_recurrence() =>
        _editor.ApplyPatch(Weekly, new EventPatch { ClearRecurrence = true }, UnmodifiedMonday, Alice).Error!.Kind.ShouldBe(ErrorKind.Validation);
}

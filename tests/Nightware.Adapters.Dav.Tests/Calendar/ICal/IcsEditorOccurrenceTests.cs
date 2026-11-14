using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

/// <summary>Occurrence ids as write targets: the exception component an unmodified occurrence gets (ADR 0001 §4).</summary>
public sealed class IcsEditorOccurrenceTests
{
    private const string Href = "calendars/alice/personal/weekly.ics";
    private static readonly DavRecurrenceId UnmodifiedMonday = DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 9, 8, 30, 0, TimeSpan.Zero));
    private static readonly DavRecurrenceId MovedWednesday = DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 4, 8, 30, 0, TimeSpan.Zero));
    private static readonly DavRecurrenceId ExcludedWednesday = DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 11, 8, 30, 0, TimeSpan.Zero));

    private readonly IcsEditor _editor = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
    private readonly IcalNetCalendarCodec _codec = new(TimeProvider.System);

    private static string Weekly => Fixture.Read(Path.Combine("ICal", "weekly-exception-exdate.ics"));

    private IReadOnlyList<CalendarEvent> Parse(string ics) =>
        _codec.Parse(ics, new CalendarParseOptions { Href = Href, Mailbox = new MailboxRef("bob@example.com") }).Value.Events;

    [Fact]
    public void An_unmodified_occurrence_becomes_an_exception_copied_from_the_master()
    {
        var events = Parse(_editor.MaterializeOccurrence(Weekly, UnmodifiedMonday).Value);

        var occurrence = events.Single(e => e.Id == DavItemId.Occurrence(Href, UnmodifiedMonday).ToEventId());
        occurrence.Type.ShouldBe(EventType.Exception);
        occurrence.IsCancelled.ShouldBeFalse();
        occurrence.SeriesMasterId.ShouldBe(DavItemId.Resource(Href).ToEventId());
        occurrence.Start.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 9, 9, 30, 0), "Europe/Berlin"));
        occurrence.End.ShouldBe(new DateTimeTimeZone(new DateTime(2026, 11, 9, 10, 0, 0), "Europe/Berlin"));
        occurrence.Recurrence.ShouldBeNull();
        occurrence.Subject.ShouldBe("Team standup");
        occurrence.Attendees.Count.ShouldBe(3);
        occurrence.IsReminderOn.ShouldBeTrue();
        occurrence.Location!.DisplayName.ShouldBe("Room 4.12");
        events.Single(e => e.Type == EventType.SeriesMaster).Recurrence.ShouldNotBeNull();
    }

    [Fact]
    public void The_exception_names_the_occurrence_in_the_masters_zone_without_its_recurrence()
    {
        var edited = _editor.MaterializeOccurrence(Weekly, UnmodifiedMonday).Value;

        edited.ShouldContain("RECURRENCE-ID;TZID=Europe/Berlin:20261109T093000");
        edited.Split("RRULE:FREQ=WEEKLY").Length.ShouldBe(2);
        edited.Split("EXDATE").Length.ShouldBe(2);
    }

    [Fact]
    public void An_existing_exception_is_reused()
    {
        var edited = _editor.MaterializeOccurrence(Weekly, MovedWednesday).Value;
        edited.Split("BEGIN:VEVENT").Length.ShouldBe(3);
    }

    [Theory]
    [InlineData(2026, 11, 11, 8, 30)] // excluded by EXDATE
    [InlineData(2026, 11, 10, 8, 30)] // a Tuesday: not produced by BYDAY=MO,WE
    [InlineData(2026, 11, 9, 9, 30)]  // right day, wrong time
    [InlineData(2027, 1, 4, 8, 30)]   // after UNTIL
    public void An_instance_the_series_does_not_produce_is_not_found(int year, int month, int day, int hour, int minute) =>
        _editor.MaterializeOccurrence(Weekly, DavRecurrenceId.FromUtc(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero)))
            .Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void A_single_event_has_no_occurrences() =>
        _editor.MaterializeOccurrence(Fixture.Read(Path.Combine("ICal", "reply.ics")), UnmodifiedMonday).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void An_all_day_occurrence_keeps_its_dates()
    {
        var edited = _editor.MaterializeOccurrence(Fixture.Read(Path.Combine("ICal", "all-day.ics")), DavRecurrenceId.FromDate(new DateOnly(2028, 12, 25))).Value;

        edited.ShouldContain("RECURRENCE-ID;VALUE=DATE:20281225");
        var occurrence = _codec.Parse(edited).Value.Events.Single(e => e.Type == EventType.Exception && !e.IsCancelled);
        occurrence.IsAllDay.ShouldBeTrue();
        occurrence.Start.DateTime.ShouldBe(new DateTime(2028, 12, 25));
        occurrence.End.DateTime.ShouldBe(new DateTime(2028, 12, 27));
    }

    [Fact]
    public void Excluded_occurrences_stay_excluded() =>
        _editor.SetParticipation(Weekly, "bob@example.com", RsvpResponse.Accept, ExcludedWednesday).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void Rsvp_on_an_unmodified_occurrence_answers_for_that_occurrence_only()
    {
        var edited = _editor.SetParticipation(Weekly, "bob@example.com", RsvpResponse.Decline, UnmodifiedMonday, comment: "Away that day").Value;

        var events = Parse(edited);
        var occurrence = events.Single(e => e.Id == DavItemId.Occurrence(Href, UnmodifiedMonday).ToEventId());
        occurrence.ResponseStatus.Response.ShouldBe(ResponseType.Declined);
        events.Single(e => e.Type == EventType.SeriesMaster).ResponseStatus.Response.ShouldBe(ResponseType.TentativelyAccepted);
        edited.Split("COMMENT:Away that day").Length.ShouldBe(2);
    }
}

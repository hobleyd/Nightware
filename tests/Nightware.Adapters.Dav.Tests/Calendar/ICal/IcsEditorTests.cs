using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

public sealed class IcsEditorTests
{
    private readonly IcsEditor _editor = new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
    private readonly IcalNetCalendarCodec _codec = new(TimeProvider.System);

    private static string Ics(string name) => Fixture.Read(Path.Combine("ICal", name));

    private static string Unfolded(string ics) => ics.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

    [Fact]
    public void Excluding_an_occurrence_adds_an_exdate_in_the_masters_zone()
    {
        var edited = _editor.ExcludeOccurrence(Ics("weekly-exception-exdate.ics"), DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero))).Value;

        edited.ShouldContain("20261116T093000");
        edited.ShouldContain("LAST-MODIFIED:20261120T120000Z");
        var cancelled = _codec.Parse(edited).Value.Events.Where(e => e.IsCancelled).Select(e => e.OriginalStart).ToList();
        cancelled.ShouldBe([new DateTimeOffset(2026, 11, 11, 8, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 8, 30, 0, TimeSpan.Zero)], ignoreOrder: true);
    }

    [Fact]
    public void Excluding_a_modified_occurrence_drops_its_exception()
    {
        var edited = _editor.ExcludeOccurrence(Ics("weekly-exception-exdate.ics"), DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 4, 8, 30, 0, TimeSpan.Zero))).Value;
        edited.ShouldNotContain("Team standup (moved)");
        _codec.Parse(edited).Value.Events.Count(e => e.IsCancelled).ShouldBe(2);
    }

    [Fact]
    public void Excluding_an_all_day_occurrence_uses_a_date()
    {
        var edited = _editor.ExcludeOccurrence(Ics("all-day.ics"), DavRecurrenceId.FromDate(new DateOnly(2026, 12, 25))).Value;
        _codec.Parse(edited).Value.Events.Count(e => e.IsCancelled).ShouldBe(2);
    }

    [Fact]
    public void Excluding_from_a_series_with_an_unresolvable_tzid_writes_utc()
    {
        var ics = Ics("outlook-display-name-tzid.ics").Replace("SEQUENCE:0", "SEQUENCE:0" + Environment.NewLine + "RRULE:FREQ=WEEKLY;COUNT=4", StringComparison.Ordinal);
        var occurrence = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero); // 10:00 AEST a week later
        var edited = _editor.ExcludeOccurrence(ics, DavRecurrenceId.FromUtc(occurrence)).Value;

        edited.ShouldContain("EXDATE:20260717T000000Z");
        _codec.Parse(edited).Value.Events.Single(e => e.IsCancelled).OriginalStart.ShouldBe(occurrence);
    }

    [Fact]
    public void Participation_comment_only_lands_on_components_with_the_attendee()
    {
        var edited = _editor.SetParticipation(Ics("weekly-exception-exdate.ics"), "carol@example.com", RsvpResponse.Accept, comment: "ok").Value;
        edited.Split("COMMENT:ok").Length.ShouldBe(2); // the master only; the exception has no Carol
    }

    [Fact]
    public void Excluding_from_a_single_event_is_not_found() =>
        _editor.ExcludeOccurrence(Ics("reply.ics"), DavRecurrenceId.FromDate(new DateOnly(2026, 1, 1))).Error!.Kind.ShouldBe(ErrorKind.NotFound);

    [Fact]
    public void Setting_participation_keeps_everything_else()
    {
        var edited = Unfolded(_editor.SetParticipation(Ics("outlook-request.ics"), "ALICE@example.com", RsvpResponse.Accept, comment: "See you there").Value);

        edited.ShouldContain("X-MICROSOFT-CDO-OWNERAPPTID:2120869543");
        edited.ShouldContain("X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/abc");
        edited.ShouldContain("COMMENT:See you there");
        var ev = _codec.Parse(edited, new CalendarParseOptions { Mailbox = new MailboxRef("alice@example.com") }).Value.Events.Single();
        ev.ResponseStatus.Response.ShouldBe(ResponseType.Accepted);
        ev.Attendees.Single(a => a.EmailAddress.Address == "bob@example.com").Status.Response.ShouldBe(ResponseType.NotResponded);
        ev.Recurrence.ShouldNotBeNull();
        edited.ShouldContain("PARTSTAT=NEEDS-ACTION;RSVP=TRUE"); // bob is untouched
    }

    [Theory]
    [InlineData(RsvpResponse.Decline, ResponseType.Declined)]
    [InlineData(RsvpResponse.TentativelyAccept, ResponseType.TentativelyAccepted)]
    public void Participation_values(RsvpResponse response, ResponseType expected)
    {
        var edited = _editor.SetParticipation(Ics("weekly-exception-exdate.ics"), "bob@example.com", response).Value;
        _codec.Parse(edited).Value.Events.Where(e => !e.IsCancelled)
            .ShouldAllBe(e => e.Attendees.Single(a => a.EmailAddress.Address == "bob@example.com").Status.Response == expected);
    }

    [Fact]
    public void Participation_on_one_existing_exception_only()
    {
        var edited = _editor.SetParticipation(Ics("weekly-exception-exdate.ics"), "bob@example.com", RsvpResponse.Decline, DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 4, 8, 30, 0, TimeSpan.Zero))).Value;
        var events = _codec.Parse(edited).Value.Events;
        events.Single(e => e.Type == EventType.SeriesMaster).Attendees.Single(a => a.EmailAddress.Address == "bob@example.com").Status.Response.ShouldBe(ResponseType.TentativelyAccepted);
        events.Single(e => e.Type == EventType.Exception && !e.IsCancelled).Attendees.Single().Status.Response.ShouldBe(ResponseType.Declined);
    }

    [Fact]
    public void Participation_for_a_non_attendee_or_missing_instance_is_not_found()
    {
        _editor.SetParticipation(Ics("weekly-exception-exdate.ics"), "mallory@example.com", RsvpResponse.Accept).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        // A Tuesday: the Monday/Wednesday series has no such occurrence (an unmodified Monday now gets an exception).
        _editor.SetParticipation(Ics("weekly-exception-exdate.ics"), "bob@example.com", RsvpResponse.Accept, DavRecurrenceId.FromUtc(new DateTimeOffset(2026, 11, 10, 8, 30, 0, TimeSpan.Zero))).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void Cancel_sets_status_and_bumps_sequence()
    {
        var events = _codec.Parse(_editor.Cancel(Ics("weekly-exception-exdate.ics")).Value).Value.Events;
        events.ShouldAllBe(e => e.IsCancelled);
        events.Single(e => e.Type == EventType.SeriesMaster).Sequence.ShouldBe(3);
        events.Single(e => e.Type == EventType.Exception && e.Subject == "Team standup (moved)").Sequence.ShouldBe(4);
    }

    [Fact]
    public void Bump_sequence_increments_every_component()
    {
        var events = _codec.Parse(_editor.BumpSequence(Ics("dst-sydney.ics")).Value).Value.Events;
        events.ShouldAllBe(e => e.Sequence == 1);
    }

    [Fact]
    public void Invalid_input_is_a_validation_error() =>
        _editor.BumpSequence("nope").Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public void An_answer_without_a_reply_marks_the_organizer_for_the_client_and_a_later_answer_clears_it()
    {
        var silent = Unfolded(_editor.SetParticipation(Ics("outlook-request.ics"), "alice@example.com", RsvpResponse.Decline, serverReplies: false).Value);
        silent.ShouldContain("SCHEDULE-AGENT=CLIENT");
        silent.ShouldContain("PARTSTAT=DECLINED");

        var told = Unfolded(_editor.SetParticipation(silent, "alice@example.com", RsvpResponse.Accept).Value);
        told.ShouldNotContain("SCHEDULE-AGENT");
    }

    [Fact]
    public void A_reply_carries_the_meeting_the_attendee_alone_and_the_comment()
    {
        var reply = Unfolded(_editor.Reply(Ics("outlook-request.ics"), "ALICE@example.com", RsvpResponse.Accept, null, "See you there", null).Value);

        reply.ShouldContain("METHOD:REPLY");
        reply.ShouldContain("UID:040000008200E00074C5B7101A82E00800000000A0B1C2D3E4F5D901000000000000000010000000F1E2D3C4B5A69788");
        reply.ShouldContain("mailto:dana@contoso.com");
        reply.ShouldContain("PARTSTAT=ACCEPTED");
        reply.ShouldContain("mailto:alice@example.com");
        reply.ShouldNotContain("bob@example.com");
        reply.ShouldContain("COMMENT:See you there");
        reply.ShouldContain("SEQUENCE:0");
    }

    [Fact]
    public void A_proposed_time_makes_a_counter_with_the_new_times()
    {
        var proposed = new TimeSlot(new DateTimeTimeZone(new DateTime(2026, 7, 21, 9, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 7, 21, 10, 0, 0), "UTC"));

        var counter = Unfolded(_editor.Reply(Ics("outlook-request.ics"), "alice@example.com", RsvpResponse.TentativelyAccept, null, null, proposed).Value);

        counter.ShouldContain("METHOD:COUNTER");
        counter.ShouldContain("DTSTART:20260721T090000Z");
        counter.ShouldContain("DTEND:20260721T100000Z");
        counter.ShouldContain("PARTSTAT=TENTATIVE");
        counter.ShouldNotContain("COMMENT");
    }

    [Fact]
    public void Someone_not_invited_cannot_reply() =>
        _editor.Reply(Ics("outlook-request.ics"), "eve@example.com", RsvpResponse.Accept, null, null, null).Error!.Kind.ShouldBe(ErrorKind.NotFound);
}

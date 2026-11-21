using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

public sealed class IcsEditorReminderTests : IDisposable
{
    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//test//EN
        BEGIN:VEVENT
        UID:remind-1
        DTSTAMP:20261120T000000Z
        DTSTART:20261201T100000Z
        DTEND:20261201T103000Z
        SUMMARY:Remind me
        BEGIN:VALARM
        ACTION:DISPLAY
        DESCRIPTION:Remind me
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        END:VCALENDAR
        """;

    private readonly CalendarAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Nightware.Domain.Calendar.CalendarEvent Parse(string ics) =>
        _h.Codec.Parse(ics, new CalendarParseOptions { Href = _h.Dav.Paths.ToRelative(AlicePersonal + "r.ics")!, Mailbox = AliceMailbox }).Value.Events[0];

    [Fact]
    public void A_snooze_acknowledges_the_alarm_and_adds_one_at_the_new_time()
    {
        var until = new DateTimeOffset(2026, 12, 1, 9, 50, 0, TimeSpan.Zero);

        var text = _h.Editor.SnoozeReminder(Ics, until).Value;
        text.ShouldContain("TRIGGER;VALUE=DATE-TIME:20261201T095000Z");
        var snoozed = Parse(text);

        snoozed.ReminderSnoozedUntil.ShouldBe(until);
        snoozed.ReminderAcknowledged.ShouldNotBeNull();
        snoozed.IsReminderOn.ShouldBeTrue();
        snoozed.ReminderMinutesBeforeStart.ShouldBe(15);
    }

    [Fact]
    public void A_dismissal_acknowledges_every_alarm_including_the_snooze()
    {
        var snoozed = _h.Editor.SnoozeReminder(Ics, DateTimeOffset.UtcNow.AddMinutes(5)).Value;

        var dismissed = Parse(_h.Editor.DismissReminder(snoozed).Value);

        dismissed.ReminderSnoozedUntil.ShouldBeNull();
        dismissed.ReminderAcknowledged.ShouldNotBeNull();
    }

    [Fact]
    public void An_event_without_an_alarm_cannot_be_snoozed()
    {
        var bare = Ics.Replace("BEGIN:VALARM\nACTION:DISPLAY\nDESCRIPTION:Remind me\nTRIGGER:-PT15M\nEND:VALARM\n", string.Empty, StringComparison.Ordinal);

        _h.Editor.SnoozeReminder(bare, DateTimeOffset.UtcNow).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}

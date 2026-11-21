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

public sealed class IcsEditorAttachmentTests : IDisposable
{
    private const string Ics = "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//t//EN\nBEGIN:VEVENT\nUID:a1\nDTSTAMP:20261120T000000Z\nDTSTART:20261201T100000Z\nDTEND:20261201T103000Z\nSUMMARY:With file\nEND:VEVENT\nEND:VCALENDAR\n";
    private static readonly Uri File = new("https://cloud.example/remote.php/dav/files/alice/Nightware/Event%20attachments/1/agenda.txt");
    private readonly CalendarAdapterHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void An_attachment_is_added_with_its_name_type_and_size_then_removed()
    {
        var added = _h.Editor.AddAttachment(Ics, new EventAttachmentLink(File, "agenda.txt", "text/plain", 12)).Value;

        IcsEditor.ReadAttachments(added).Value.ShouldBe([new EventAttachmentLink(File, "agenda.txt", "text/plain", 12)]);
        var removed = _h.Editor.RemoveAttachment(added, File).Value;
        IcsEditor.ReadAttachments(removed).Value.ShouldBeEmpty();
        _h.Editor.RemoveAttachment(removed, File).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

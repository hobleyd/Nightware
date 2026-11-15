using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.ICal;

/// <summary>
/// Ical.Net throws when asked for a VTIMEZONE whose earliest date is more than about a year ahead, which made
/// any meeting created (or moved) that far out a 500. Found by the live calendar tests.
/// </summary>
public sealed class FarFutureZoneTests
{
    private static readonly DateTime FarAhead = DateTime.UtcNow.Date.AddYears(3);

    [Fact]
    public void A_vtimezone_never_starts_after_today()
    {
        ZoneWriter.SupportedFrom(FarAhead).ShouldBe(DateTime.UtcNow.Date);
        ZoneWriter.SupportedFrom(new DateTime(2020, 5, 2)).ShouldBe(new DateTime(2020, 5, 1));
    }

    [Fact]
    public void An_event_years_ahead_serializes_in_its_zone()
    {
        var codec = new IcalNetCalendarCodec(TimeProvider.System);
        var start = new DateTimeTimeZone(FarAhead.AddHours(9), "Australia/Sydney");
        var ics = codec.Serialize(new CalendarDocument(null, [new CalendarEvent(new EventId(string.Empty), "Far", start, start with { DateTime = start.DateTime.AddHours(1) }) { ICalUId = "far" }]));

        codec.Parse(ics).Value.Events.Single().Start.ShouldBe(start);
    }

    [Fact]
    public void An_event_moved_years_ahead_into_a_new_zone_is_patched()
    {
        var editor = new IcsEditor(TimeProvider.System);
        var start = new DateTimeTimeZone(FarAhead.AddHours(9), "Asia/Tokyo");

        var edited = editor.ApplyPatch(Fixture.Read(Path.Combine("ICal", "reply.ics")), new EventPatch { Start = start }, null, "alice@example.com").Value;

        edited.ShouldContain("TZID:Asia/Tokyo");
    }
}

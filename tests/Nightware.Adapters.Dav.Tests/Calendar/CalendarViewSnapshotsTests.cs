using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Dav.Calendar;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class CalendarViewSnapshotsTests
{
    private static readonly DateTimeOffset Start = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(int n) =>
        new(new EventId("e" + n), "Item " + n, DateTimeTimeZone.Utc(Start.AddHours(n)), DateTimeTimeZone.Utc(Start.AddHours(n + 1)));

    [Fact]
    public void A_view_is_kept_for_a_minute()
    {
        var time = new FakeTimeProvider(Start);
        using var snapshots = new CalendarViewSnapshots(time);
        var key = CalendarViewSnapshots.KeyFor("/principals/users/alice/", "calendars/alice/personal/", Start, Start.AddDays(7));
        IReadOnlyList<CalendarEvent> view = [Event(1), Event(2)];

        snapshots.Put(key, view);
        snapshots.Get(key).ShouldBeSameAs(view);

        time.Advance(CalendarViewSnapshots.Lifetime + TimeSpan.FromSeconds(1));
        snapshots.Get(key).ShouldBeNull();
    }

    [Fact]
    public void Callers_calendars_and_windows_have_their_own_keys()
    {
        var alice = CalendarViewSnapshots.KeyFor("/principals/users/alice/", "calendars/bob/shared/", Start, Start.AddDays(7));

        alice.ShouldNotBe(CalendarViewSnapshots.KeyFor("/principals/users/carol/", "calendars/bob/shared/", Start, Start.AddDays(7)));
        alice.ShouldNotBe(CalendarViewSnapshots.KeyFor("/principals/users/alice/", "calendars/bob/other/", Start, Start.AddDays(7)));
        alice.ShouldNotBe(CalendarViewSnapshots.KeyFor("/principals/users/alice/", "calendars/bob/shared/", Start, Start.AddDays(8)));
    }

    [Fact]
    public void A_window_too_large_to_keep_is_not_kept()
    {
        using var snapshots = new CalendarViewSnapshots(new FakeTimeProvider(Start));
        var huge = Enumerable.Range(0, (CalendarViewSnapshots.MaxInstances / 4) + 1).Select(Event).ToList();

        snapshots.Put("k", huge);

        snapshots.Get("k").ShouldBeNull();
    }
}

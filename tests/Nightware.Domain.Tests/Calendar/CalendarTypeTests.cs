using Nightware.Domain.Calendar;

namespace Nightware.Domain.Tests.Calendar;

public sealed class CalendarTypeTests
{
    [Fact]
    public void Utc_date_time_time_zone_uses_the_utc_wall_clock()
    {
        var value = DateTimeTimeZone.Utc(new DateTimeOffset(2026, 7, 1, 19, 30, 0, TimeSpan.FromHours(10)));

        value.DateTime.ShouldBe(new DateTime(2026, 7, 1, 9, 30, 0));
        value.TimeZone.ShouldBe("UTC");
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, false)]
    public void Event_patch_knows_when_the_schedule_changes(bool start, bool end, bool allDay, bool recurrence, bool changes)
    {
        var when = DateTimeTimeZone.Utc(DateTimeOffset.UnixEpoch);
        var patch = new EventPatch
        {
            Subject = "only the subject",
            Start = start ? when : null,
            End = end ? when : null,
            IsAllDay = allDay ? true : null,
            Recurrence = recurrence ? new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 1), new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 1, 1))) : null,
        };

        patch.ChangesSchedule.ShouldBe(changes);
    }

    [Fact]
    public void New_events_default_to_a_fifteen_minute_reminder()
    {
        var e = new NewEvent("s", DateTimeTimeZone.Utc(DateTimeOffset.UnixEpoch), DateTimeTimeZone.Utc(DateTimeOffset.UnixEpoch));

        e.IsReminderOn.ShouldBeTrue();
        e.ReminderMinutesBeforeStart.ShouldBe(15);
        e.ShowAs.ShouldBe(ShowAs.Busy);
    }

    [Fact]
    public void Free_busy_status_values_match_availability_view_digits()
    {
        ((int)FreeBusyStatus.Free).ShouldBe(0);
        ((int)FreeBusyStatus.Tentative).ShouldBe(1);
        ((int)FreeBusyStatus.Busy).ShouldBe(2);
        ((int)FreeBusyStatus.Oof).ShouldBe(3);
        ((int)FreeBusyStatus.WorkingElsewhere).ShouldBe(4);
    }
}

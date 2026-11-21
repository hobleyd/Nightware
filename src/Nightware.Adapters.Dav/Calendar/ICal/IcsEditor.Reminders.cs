using System.Globalization;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>Reminder state in RFC 9074 terms, which Sabre stores as it is.</summary>
public sealed partial class IcsEditor
{
    /// <summary>
    /// Dismisses every alarm, snoozes included: <c>ACKNOWLEDGED</c> is now, or <paramref name="dueAt"/> when later (a
    /// reminder dismissed before it fires is done with too), so fire times up to it are done.
    /// </summary>
    public Result<string> DismissReminder(string ics, DateTimeOffset? dueAt = null) =>
        Edit(ics, calendar =>
        {
            var current = _timeProvider.GetUtcNow();
            var now = Stamp(dueAt is { } due && due > current ? due : current);
            foreach (var alarm in calendar.Events.SelectMany(static e => e.Alarms))
            {
                alarm.Properties.Set("ACKNOWLEDGED", now);
            }

            return Result.Success();
        });

    /// <summary>
    /// Snoozes the reminder until <paramref name="until"/>: the alarms are acknowledged and a snooze alarm (RFC 9074
    /// §5) fires at that time. An earlier, unacknowledged snooze is replaced.
    /// </summary>
    public Result<string> SnoozeReminder(string ics, DateTimeOffset until) =>
        Edit(ics, calendar =>
        {
            var master = calendar.Events.FirstOrDefault(static e => e.RecurrenceIdentifier is null) ?? calendar.Events.FirstOrDefault();
            if (master is null || master.Alarms.Count == 0)
            {
                return Error.Validation("The event has no reminder to snooze.");
            }

            var now = Stamp(_timeProvider.GetUtcNow());
            foreach (var old in master.Alarms.Where(IcalNetCalendarCodec.IsSnooze).ToList())
            {
                master.Alarms.Remove(old);
            }

            foreach (var alarm in master.Alarms)
            {
                alarm.Properties.Set("ACKNOWLEDGED", now);
            }

            var original = master.Alarms[0]!;
            var uid = original.Properties.Get<string>("UID");
            if (uid is null)
            {
                uid = Guid.NewGuid().ToString();
                original.Properties.Set("UID", uid);
            }

            var snooze = new Alarm
            {
                Action = AlarmAction.Display,
                Description = master.Summary ?? "Reminder",
            };

            // Ical.Net writes an empty TRIGGER for an absolute time, so the property is written as text.
            var trigger = new CalendarProperty("TRIGGER", Stamp(until));
            trigger.Parameters.Set("VALUE", "DATE-TIME");
            snooze.Properties.Add(trigger);
            snooze.Properties.Set("UID", Guid.NewGuid().ToString());
            var related = new CalendarProperty("RELATED-TO", uid);
            related.Parameters.Set("RELTYPE", "SNOOZE");
            snooze.Properties.Add(related);
            master.Alarms.Add(snooze);
            return Result.Success();
        });

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}

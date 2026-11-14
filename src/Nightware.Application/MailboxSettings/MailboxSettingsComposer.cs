using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary>
/// Joins the two stores behind <c>mailboxSettings</c> and expresses the automatic-reply schedule
/// in the mailbox's own zone. NightMail reads <c>scheduledStart/EndDateTime</c> as wall clock with
/// no conversion and writes them back the same way, so a schedule must come back in the zone
/// <c>timeZone</c> names (Graph does the same).
/// </summary>
internal static class MailboxSettingsComposer
{
    /// <param name="preferences">Null when the directory has no entry for the mailbox (e.g. a shared mailbox): UTC.</param>
    public static MailboxSettingsSnapshot Compose(AutoReplySetting replies, UserPreferences? preferences)
    {
        var zoneName = preferences?.TimeZone is { } stored && GraphTimeZone.TryFind(stored, out _)
            ? GraphTimeZone.ToWindowsId(stored)
            : GraphTimeZone.Utc;
        GraphTimeZone.TryFind(zoneName, out var zone);
        return new MailboxSettingsSnapshot(
            replies with
            {
                ScheduledStartDateTime = InZone(replies.ScheduledStartDateTime, zone, zoneName),
                ScheduledEndDateTime = InZone(replies.ScheduledEndDateTime, zone, zoneName),
            },
            zoneName);
    }

    /// <summary>The preferences, or null when the directory does not know the mailbox; other failures pass through.</summary>
    public static Result<UserPreferences?> OrNone(Result<UserPreferences> preferences) =>
        preferences.IsSuccess
            ? preferences.Value
            : preferences.Error.Kind == ErrorKind.NotFound ? Result<UserPreferences?>.Success(null) : Result<UserPreferences?>.Failure(preferences.Error);

    private static DateTimeTimeZone? InZone(DateTimeTimeZone? value, TimeZoneInfo zone, string zoneName) =>
        value is null
            ? null
            : new DateTimeTimeZone(GraphTimeZone.ToWallClock(GraphTimeZone.ToInstant(value.DateTime, value.TimeZone), zone), zoneName);
}

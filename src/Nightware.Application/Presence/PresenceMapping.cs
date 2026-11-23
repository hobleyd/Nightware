using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Application.Presence;

/// <summary>The status a Graph availability and activity pair sets: a status type and, for some, a predefined message.</summary>
public sealed record StatusChange(UserStatusType Type, string? MessageId);

/// <summary>Nextcloud user status ↔ Graph presence, per ADR 0009 D6.</summary>
public static class PresenceMapping
{
    /// <summary>
    /// The pairs <c>setPresence</c> and <c>setUserPreferredPresence</c> accept: the union of Graph's two tables, plus
    /// <c>Busy</c>/<c>InAMeeting</c>, which Nextcloud has a message for. Nextcloud has one status, so both set it.
    /// </summary>
    private static readonly Dictionary<(string Availability, string Activity), StatusChange> Settable = new(PairComparer.Instance)
    {
        [(Availability.Available, Activity.Available)] = new(UserStatusType.Online, null),
        [(Availability.Away, Activity.Away)] = new(UserStatusType.Away, null),
        [(Availability.BeRightBack, Activity.BeRightBack)] = new(UserStatusType.Away, PredefinedMessages.BeRightBack),
        [(Availability.Busy, Activity.Busy)] = new(UserStatusType.Busy, null),
        [(Availability.Busy, Activity.InACall)] = new(UserStatusType.Busy, PredefinedMessages.Call),
        [(Availability.Busy, Activity.InAConferenceCall)] = new(UserStatusType.Busy, PredefinedMessages.Call),
        [(Availability.Busy, Activity.InAMeeting)] = new(UserStatusType.Busy, PredefinedMessages.Meeting),
        [(Availability.DoNotDisturb, Activity.DoNotDisturb)] = new(UserStatusType.DoNotDisturb, null),
        [(Availability.DoNotDisturb, Activity.Presenting)] = new(UserStatusType.DoNotDisturb, null),
        [(Availability.Offline, Activity.OffWork)] = new(UserStatusType.Invisible, null),
    };

    /// <summary>Reads a status as Graph presence.</summary>
    /// <param name="own">The caller's own status: <c>invisible</c> reads <c>OffWork</c>, and the message keeps its expiry.</param>
    public static UserPresence ToPresence(string id, UserStatus status, bool own, OutOfOfficeSettings outOfOffice)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(outOfOffice);
        var (availability, activity) = AvailabilityOf(status, own);
        return new UserPresence(id, availability, activity) { StatusMessage = MessageOf(status, own), OutOfOffice = outOfOffice };
    }

    /// <summary>The status a Graph pair sets; a pair neither of Graph's tables lists is a 400.</summary>
    public static Result<StatusChange> ToStatus(string? availability, string? activity)
    {
        if (string.IsNullOrWhiteSpace(availability) || string.IsNullOrWhiteSpace(activity))
        {
            return Error.Validation("availability and activity are required.");
        }

        return Settable.TryGetValue((availability.Trim(), activity.Trim()), out var change)
            ? change
            : Error.Validation($"The availability '{availability}' with activity '{activity}' is not supported.", "activity");
    }

    private static (string Availability, string Activity) AvailabilityOf(UserStatus status, bool own)
    {
        var message = MessageIdOf(status, own);
        return status.Type switch
        {
            UserStatusType.Online or UserStatusType.Away when message == PredefinedMessages.BeRightBack => (Availability.BeRightBack, Activity.BeRightBack),
            UserStatusType.Online => (Availability.Available, Activity.Available),
            UserStatusType.Away => (Availability.Away, Activity.Away),
            UserStatusType.Busy when message == PredefinedMessages.Call => (Availability.Busy, Activity.InACall),
            UserStatusType.Busy when message == PredefinedMessages.Meeting => (Availability.Busy, Activity.InAMeeting),
            UserStatusType.Busy => (Availability.Busy, Activity.Busy),
            UserStatusType.DoNotDisturb => (Availability.DoNotDisturb, Activity.DoNotDisturb),
            UserStatusType.Invisible when own => (Availability.Offline, Activity.OffWork),
            _ => (Availability.Offline, Activity.Offline),
        };
    }

    /// <summary>
    /// The message reads as <c>statusMessage</c> unless it stands for an activity. Another user's has no expiry, as in
    /// Graph; an icon set with it leads the text, as Nextcloud shows it.
    /// </summary>
    private static PresenceStatusMessage? MessageOf(UserStatus status, bool own)
    {
        if (string.IsNullOrWhiteSpace(status.Message) || PredefinedMessages.IsActivity(MessageIdOf(status, own)))
        {
            return null;
        }

        var content = string.IsNullOrWhiteSpace(status.Icon) ? status.Message.Trim() : $"{status.Icon} {status.Message.Trim()}";
        return new PresenceStatusMessage(content) { ExpiryDateTime = own ? status.ClearAt : null };
    }

    /// <summary>
    /// The predefined message id: the caller's own status names it. Another user's public status doesn't, so a predefined
    /// message is known by its icon (the text is in the reader's language).
    /// </summary>
    private static string? MessageIdOf(UserStatus status, bool own) =>
        own || status.MessageId is not null ? status.MessageId : status.Icon switch
        {
            PredefinedMessages.CallIcon => PredefinedMessages.Call,
            PredefinedMessages.MeetingIcon => PredefinedMessages.Meeting,
            PredefinedMessages.BeRightBackIcon => PredefinedMessages.BeRightBack,
            _ => null,
        };

    private sealed class PairComparer : IEqualityComparer<(string Availability, string Activity)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Availability, string Activity) x, (string Availability, string Activity) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Availability, y.Availability) && StringComparer.OrdinalIgnoreCase.Equals(x.Activity, y.Activity);

        public int GetHashCode((string Availability, string Activity) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Availability), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Activity));
    }
}

namespace Nightware.Domain.Presence;

/// <summary>Nextcloud's user status types (<c>user_status</c>).</summary>
public enum UserStatusType
{
    Online,
    Away,
    DoNotDisturb,
    Busy,
    Invisible,
    Offline,
}

/// <summary>
/// A user's Nextcloud status: the type, and the status message with its icon and expiry. Nextcloud has one status per
/// user, not one per application (ADR 0009 D6).
/// </summary>
public sealed record UserStatus(UserStatusType Type)
{
    /// <summary>A user who has never set a status, or whose status expired: Nextcloud has no row for them.</summary>
    public static UserStatus None { get; } = new(UserStatusType.Offline);

    /// <summary>
    /// The predefined message's id (<see cref="PredefinedMessages"/>), or null for a custom message or none. Only the
    /// user's own status carries it; another user's is recognised by the predefined message's icon.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>The message as Nextcloud shows it (a predefined message in the reader's language).</summary>
    public string? Message { get; init; }

    public string? Icon { get; init; }

    /// <summary>When Nextcloud clears the message, and with it the status.</summary>
    public DateTimeOffset? ClearAt { get; init; }
}

/// <summary>The predefined message ids that carry a Graph activity (ADR 0009 D6), with their icons.</summary>
public static class PredefinedMessages
{
    /// <summary>Talk's "In a call", set while the user is in a call.</summary>
    public const string Call = "call";

    /// <summary>"In a meeting", set by hand or by Nextcloud's calendar while an event is on.</summary>
    public const string Meeting = "meeting";

    public const string BeRightBack = "be-right-back";

    public const string CallIcon = "\U0001F4AC";
    public const string MeetingIcon = "\U0001F4C5";
    public const string BeRightBackIcon = "⏳";

    /// <summary>The messages the façade sets for an activity, and clears again when presence is cleared.</summary>
    public static bool IsActivity(string? messageId) =>
        messageId is Call or Meeting or BeRightBack;
}

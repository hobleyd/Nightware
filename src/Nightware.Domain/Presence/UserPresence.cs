namespace Nightware.Domain.Presence;

/// <summary>Graph <c>presence</c>: a user's availability and activity, status message and out-of-office state.</summary>
/// <param name="Id">The user's directory id.</param>
/// <param name="Availability">One of <see cref="Availability"/>'s values.</param>
/// <param name="Activity">One of <see cref="Activity"/>'s values.</param>
public sealed record UserPresence(string Id, string Availability, string Activity)
{
    public PresenceStatusMessage? StatusMessage { get; init; }

    public OutOfOfficeSettings OutOfOffice { get; init; } = OutOfOfficeSettings.NotOutOfOffice;
}

/// <summary>Graph <c>presenceStatusMessage</c>: a plain-text message and, for the caller's own, when it expires.</summary>
public sealed record PresenceStatusMessage(string Content)
{
    public DateTimeOffset? ExpiryDateTime { get; init; }
}

/// <summary>Graph <c>outOfOfficeSettings</c>.</summary>
public sealed record OutOfOfficeSettings(bool IsOutOfOffice, string? Message)
{
    public static OutOfOfficeSettings NotOutOfOffice { get; } = new(false, null);
}

/// <summary>Graph's <c>availability</c> values, spelt as Graph writes them.</summary>
public static class Availability
{
    public const string Available = "Available";
    public const string Away = "Away";
    public const string BeRightBack = "BeRightBack";
    public const string Busy = "Busy";
    public const string DoNotDisturb = "DoNotDisturb";
    public const string Offline = "Offline";
}

/// <summary>Graph's <c>activity</c> values the façade reads or accepts.</summary>
public static class Activity
{
    public const string Available = "Available";
    public const string Away = "Away";
    public const string BeRightBack = "BeRightBack";
    public const string Busy = "Busy";
    public const string InACall = "InACall";
    public const string InAConferenceCall = "InAConferenceCall";
    public const string InAMeeting = "InAMeeting";
    public const string DoNotDisturb = "DoNotDisturb";
    public const string Presenting = "Presenting";
    public const string Offline = "Offline";
    public const string OffWork = "OffWork";
}

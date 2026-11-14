using Nightware.Domain.Calendar;

namespace Nightware.Domain.MailboxSettings;

public enum AutoReplyStatus
{
    Disabled,
    AlwaysEnabled,
    Scheduled,
}

public enum ExternalAudience
{
    None,
    ContactsOnly,
    All,
}

/// <summary>Graph <c>automaticRepliesSetting</c>.</summary>
public sealed record AutoReplySetting(AutoReplyStatus Status)
{
    public ExternalAudience ExternalAudience { get; init; } = ExternalAudience.All;

    public string? InternalReplyMessage { get; init; }

    public string? ExternalReplyMessage { get; init; }

    public DateTimeTimeZone? ScheduledStartDateTime { get; init; }

    public DateTimeTimeZone? ScheduledEndDateTime { get; init; }
}

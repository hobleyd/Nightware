namespace Nightware.Domain.MailboxSettings;

/// <summary>
/// The mailbox settings NightMail reads (Graph <c>mailboxSettings</c>, restricted to
/// <c>automaticRepliesSetting</c> and <c>timeZone</c>).
/// </summary>
/// <param name="AutomaticReplies">Scheduled times are expressed in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">The mailbox's Windows time-zone id (Graph's form), e.g. <c>AUS Eastern Standard Time</c>.</param>
public sealed record MailboxSettingsSnapshot(AutoReplySetting AutomaticReplies, string TimeZone);

/// <summary>A <c>PATCH mailboxSettings</c>. Null members are left unchanged.</summary>
public sealed record MailboxSettingsPatch
{
    /// <summary>Replaces the whole setting (Graph replaces complex properties rather than merging them).</summary>
    public AutoReplySetting? AutomaticReplies { get; init; }

    /// <summary>A Windows or IANA time-zone id.</summary>
    public string? TimeZone { get; init; }

    public bool IsEmpty => AutomaticReplies is null && TimeZone is null;
}

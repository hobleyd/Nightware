namespace Nightware.Domain.MailboxSettings;

/// <summary>
/// The mailbox settings NightMail reads (Graph <c>mailboxSettings</c>, restricted to
/// <c>automaticRepliesSetting</c> and <c>timeZone</c>).
/// </summary>
/// <param name="AutomaticReplies">Scheduled times are expressed in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">The mailbox's Windows time-zone id (Graph's form), e.g. <c>AUS Eastern Standard Time</c>.</param>
public sealed record MailboxSettingsSnapshot(AutoReplySetting AutomaticReplies, string TimeZone)
{
    /// <summary>The mailbox's language (Graph <c>language</c>), its own or <c>en-US</c>.</summary>
    public MailboxLanguage Language { get; init; } = MailboxLanguage.Default;

    /// <summary>Its own working hours, or Exchange's defaults in <see cref="TimeZone"/>.</summary>
    public Nightware.Domain.Directory.WorkingHours? WorkingHours { get; init; }

    /// <summary>A .NET short date pattern (Graph <c>dateFormat</c>): its own, or its language's.</summary>
    public string DateFormat { get; init; } = "M/d/yyyy";

    /// <summary>A .NET short time pattern (Graph <c>timeFormat</c>): its own, or its language's.</summary>
    public string TimeFormat { get; init; } = "h:mm tt";
}

/// <summary>Graph <c>localeInfo</c>: a locale and its name in that language.</summary>
public sealed record MailboxLanguage(string Locale, string DisplayName)
{
    public static readonly MailboxLanguage Default = new("en-US", "English (United States)");
}

/// <summary>A <c>PATCH mailboxSettings</c>. Null members are left unchanged.</summary>
public sealed record MailboxSettingsPatch
{
    /// <summary>Replaces the whole setting (Graph replaces complex properties rather than merging them).</summary>
    public AutoReplySetting? AutomaticReplies { get; init; }

    /// <summary>A Windows or IANA time-zone id.</summary>
    public string? TimeZone { get; init; }

    public bool IsEmpty => AutomaticReplies is null && TimeZone is null;
}

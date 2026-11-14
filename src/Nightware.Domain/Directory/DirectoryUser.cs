namespace Nightware.Domain.Directory;

/// <summary>A user in the directory (Graph <c>user</c>).</summary>
public sealed record DirectoryUser(string Id, string DisplayName, string UserPrincipalName)
{
    public string? Mail { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public string? CompanyName { get; init; }

    public string? OfficeLocation { get; init; }

    public string? MobilePhone { get; init; }

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    /// <summary>
    /// Non-SMTP proxy addresses in Exchange form (<c>SIP:a@b</c>), emitted verbatim after the SMTP
    /// entries the Api builds from <see cref="Mail"/> and <see cref="Aliases"/>.
    /// </summary>
    public IReadOnlyList<string> ProxyAddresses { get; init; } = [];

    /// <summary>
    /// Secondary SMTP addresses of the user's mailbox (bare addresses, without <see cref="Mail"/>).
    /// The Api renders them as Graph <c>proxyAddresses</c> <c>smtp:</c> entries.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record WorkingHours(IReadOnlyList<DayOfWeek> DaysOfWeek, TimeOnly StartTime, TimeOnly EndTime, string TimeZone);

/// <summary>Mailbox preferences stored in the directory (timeZone, language, working hours).</summary>
public sealed record UserPreferences(string? TimeZone, string? Language)
{
    public WorkingHours? WorkingHours { get; init; }

    public string? DateFormat { get; init; }

    public string? TimeFormat { get; init; }
}

public sealed record UserPreferencesPatch
{
    public string? TimeZone { get; init; }

    public string? Language { get; init; }

    public WorkingHours? WorkingHours { get; init; }
}

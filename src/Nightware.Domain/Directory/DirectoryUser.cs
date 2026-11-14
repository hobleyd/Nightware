namespace Nightware.Domain.Directory;

/// <summary>A user in the directory (Graph <c>user</c>).</summary>
public sealed record DirectoryUser(string Id, string DisplayName, string UserPrincipalName)
{
    public string? Mail { get; init; }

    public string? GivenName { get; init; }

    public string? Surname { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public string? OfficeLocation { get; init; }

    public string? MobilePhone { get; init; }

    public IReadOnlyList<string> BusinessPhones { get; init; } = [];

    public IReadOnlyList<string> ProxyAddresses { get; init; } = [];
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

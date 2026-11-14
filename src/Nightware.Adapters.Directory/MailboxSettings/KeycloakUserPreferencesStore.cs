using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.MailboxSettings;

/// <summary>
/// <see cref="IUserPreferencesStore"/> over Keycloak user attributes (ADR 0001 §29), read and written
/// with the <c>nightware-api</c> service account. The mailbox's user is found by exact email.
/// </summary>
/// <remarks>
/// The service account can see and change every user, so this store does no authorisation of its
/// own: callers must establish that the signed-in user may act on the mailbox first (the
/// mailbox-settings use cases do, through the mail store). Values are stored as given; time-zone
/// normalisation is the caller's.
/// </remarks>
internal sealed class KeycloakUserPreferencesStore(IKeycloakAdminClient admin) : IUserPreferencesStore
{
    public const string TimeZoneAttribute = "zoneinfo";
    public const string LocaleAttribute = "locale";
    public const string WorkingHoursAttribute = "nightware_working_hours";
    public const string DateFormatAttribute = "nightware_date_format";
    public const string TimeFormatAttribute = "nightware_time_format";

    private const string TimeOfDayFormat = "HH:mm:ss.fffffff";

    public async Task<Result<UserPreferences>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var user = await FindAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return user.Map(ToPreferences);
    }

    public async Task<Result<UserPreferences>> SetAsync(MailboxRef mailbox, UserPreferencesPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var user = await FindAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        var changes = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        if (patch.TimeZone is { } zone)
        {
            changes[TimeZoneAttribute] = [zone];
        }

        if (patch.Language is { } locale)
        {
            changes[LocaleAttribute] = [locale];
        }

        if (patch.WorkingHours is { } hours)
        {
            changes[WorkingHoursAttribute] = [WriteWorkingHours(hours)];
        }

        if (changes.Count == 0)
        {
            return ToPreferences(user.Value);
        }

        var updated = await admin.UpdateUserAttributesAsync(user.Value.Id, changes, cancellationToken).ConfigureAwait(false);
        return updated.Map(ToPreferences);
    }

    internal static UserPreferences ToPreferences(KeycloakUser user) =>
        new(Blank(user.Attribute(TimeZoneAttribute)), Blank(user.Attribute(LocaleAttribute)))
        {
            WorkingHours = ReadWorkingHours(user.Attribute(WorkingHoursAttribute)),
            DateFormat = Blank(user.Attribute(DateFormatAttribute)),
            TimeFormat = Blank(user.Attribute(TimeFormatAttribute)),
        };

    /// <summary>Graph's <c>workingHours</c> JSON; anything unreadable counts as unset rather than failing the whole read.</summary>
    internal static WorkingHours? ReadWorkingHours(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(json);
            var days = node?["daysOfWeek"]?.AsArray()
                .Select(d => Enum.TryParse<DayOfWeek>(d?.GetValue<string>(), ignoreCase: true, out var day) ? day : (DayOfWeek?)null)
                .ToList();
            if (days is null || days.Any(d => d is null)
                || !TimeOnly.TryParse(node?["startTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var start)
                || !TimeOnly.TryParse(node?["endTime"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var end))
            {
                return null;
            }

            return new WorkingHours([.. days.Select(d => d!.Value)], start, end, node?["timeZone"]?["name"]?.GetValue<string>() ?? "UTC");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    internal static string WriteWorkingHours(WorkingHours hours) =>
        new JsonObject
        {
            ["daysOfWeek"] = new JsonArray([.. hours.DaysOfWeek.Select(d => (JsonNode?)JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(d.ToString())))]),
            ["startTime"] = hours.StartTime.ToString(TimeOfDayFormat, CultureInfo.InvariantCulture),
            ["endTime"] = hours.EndTime.ToString(TimeOfDayFormat, CultureInfo.InvariantCulture),
            ["timeZone"] = new JsonObject { ["name"] = hours.TimeZone },
        }.ToJsonString();

    private async Task<Result<KeycloakUser>> FindAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(mailbox.Address)
            ? Error.NotFound("No mailbox address given.")
            : await admin.FindUserByEmailAsync(mailbox.Address, cancellationToken).ConfigureAwait(false);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

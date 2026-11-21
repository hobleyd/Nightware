using Nightware.Domain.Kernel;

namespace Nightware.Application.MailboxSettings;

/// <summary>Graph <c>timeZoneInformation</c>: a Windows id, its display name, and the IANA id it maps to.</summary>
public sealed record SupportedTimeZone(string Alias, string DisplayName, string IanaId);

/// <summary>Graph <c>localeInfo</c>, with the language's short date and time patterns.</summary>
public sealed record SupportedLanguage(string Locale, string DisplayName, string DateFormat, string TimeFormat);

/// <summary><c>outlook/supportedTimeZones</c>'s <c>TimeZoneStandard</c>.</summary>
public enum TimeZoneStandard
{
    Windows,
    Iana,
}

/// <summary>
/// The time zones and languages Nightware offers (<c>outlook/supportedTimeZones</c>, <c>supportedLanguages</c>). Fixed
/// data, generated once by <c>tools/outlook-catalog/generate.cs</c>: the server never takes names from the host.
/// </summary>
public static partial class OutlookCatalog
{
    /// <summary>A known language by locale (case-insensitive), or null.</summary>
    public static SupportedLanguage? FindLanguage(string? locale) =>
        string.IsNullOrWhiteSpace(locale) ? null : Languages.FirstOrDefault(l => string.Equals(l.Locale, locale, StringComparison.OrdinalIgnoreCase));

    /// <summary>The function argument as Graph sends it: <c>microsoft.graph.timeZoneStandard'Iana'</c>, or just <c>Iana</c>.</summary>
    public static Result<TimeZoneStandard> ParseStandard(string? argument)
    {
        var value = (argument ?? string.Empty).Trim();
        const string prefix = "microsoft.graph.timeZoneStandard";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        value = value.Trim('\'');
        return Enum.TryParse<TimeZoneStandard>(value, ignoreCase: true, out var standard) && Enum.IsDefined(standard) && !int.TryParse(value, out _)
            ? standard
            : Error.Validation($"TimeZoneStandard '{argument}' is not supported: use Windows or Iana.", "TimeZoneStandard");
    }
}

/// <summary><c>GET outlook/supportedTimeZones</c>.</summary>
public sealed class GetSupportedTimeZones(IMailboxSettingsAccess access)
{
    public async Task<Result<IReadOnlyList<(string Alias, string DisplayName)>>> ExecuteAsync(MailboxRef mailbox, TimeZoneStandard standard, CancellationToken cancellationToken)
    {
        var allowed = await access.CheckAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!allowed.IsSuccess)
        {
            return allowed.Error!;
        }

        // IANA ids are their own display name, as Graph shows them.
        return standard == TimeZoneStandard.Iana
            ? OutlookCatalog.TimeZones.Select(static z => z.IanaId).Distinct(StringComparer.Ordinal).Select(static id => (id, id)).ToList()
            : OutlookCatalog.TimeZones.Select(static z => (z.Alias, z.DisplayName)).ToList();
    }
}

/// <summary><c>GET outlook/supportedLanguages</c>.</summary>
public sealed class GetSupportedLanguages(IMailboxSettingsAccess access)
{
    public async Task<Result<IReadOnlyList<SupportedLanguage>>> ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var allowed = await access.CheckAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return allowed.IsSuccess ? Result<IReadOnlyList<SupportedLanguage>>.Success(OutlookCatalog.Languages) : allowed.Error!;
    }
}

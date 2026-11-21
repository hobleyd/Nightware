// Writes src/Nightware.Application/MailboxSettings/OutlookCatalog.Data.cs: the time zones and languages behind
// outlook/supportedTimeZones and outlook/supportedLanguages, and each language's date and time formats.
//
//     dotnet run tools/outlook-catalog/generate.cs
//
// It runs once, on a developer machine, and its output is committed: the server never reads the host's time-zone or
// culture names, which differ between macOS and the Linux image. Windows ids are every id CLDR maps (the data
// TimeZoneInfo converts with); display names are Microsoft's ("(UTC+10:00) Canberra, Melbourne, Sydney") from the
// Windows "Default Time Zones" page where it has one, else "(UTC±hh:mm) <id>". Offsets are standard (not daylight).
using System.Globalization;
using System.Text;

var descriptions = new Dictionary<string, string>
{
    ["Afghanistan Standard Time"] = "Kabul", ["FLE Standard Time"] = "Helsinki, Kyiv, Riga, Sofia, Tallinn, Vilnius",
    ["Central Europe Standard Time"] = "Belgrade, Bratislava, Budapest, Ljubljana, Prague", ["W. Central Africa Standard Time"] = "West Central Africa",
    ["UTC-11"] = "Coordinated Universal Time-11", ["W. Europe Standard Time"] = "Amsterdam, Berlin, Bern, Rome, Stockholm, Vienna",
    ["SA Western Standard Time"] = "Georgetown, La Paz, Manaus, San Juan", ["Pacific SA Standard Time"] = "Santiago",
    ["Argentina Standard Time"] = "City of Buenos Aires", ["Caucasus Standard Time"] = "Yerevan",
    ["AUS Eastern Standard Time"] = "Canberra, Melbourne, Sydney", ["Azerbaijan Standard Time"] = "Baku",
    ["Eastern Standard Time"] = "Eastern Time (US & Canada)", ["Arab Standard Time"] = "Kuwait, Riyadh", ["Bangladesh Standard Time"] = "Dhaka",
    ["Belarus Standard Time"] = "Minsk", ["Romance Standard Time"] = "Brussels, Copenhagen, Madrid, Paris", ["Central America Standard Time"] = "Central America",
    ["Atlantic Standard Time"] = "Atlantic Time (Canada)", ["Central European Standard Time"] = "Sarajevo, Skopje, Warsaw, Zagreb",
    ["South Africa Standard Time"] = "Harare, Pretoria", ["UTC"] = "Coordinated Universal Time", ["E. South America Standard Time"] = "Brasilia",
    ["Central Asia Standard Time"] = "Astana", ["Singapore Standard Time"] = "Kuala Lumpur, Singapore", ["Greenwich Standard Time"] = "Monrovia, Reykjavik",
    ["Cape Verde Standard Time"] = "Cabo Verde Is.", ["SE Asia Standard Time"] = "Bangkok, Hanoi, Jakarta", ["SA Pacific Standard Time"] = "Bogota, Lima, Quito, Rio Branco",
    ["China Standard Time"] = "Beijing, Chongqing, Hong Kong SAR, Urumqi", ["Myanmar Standard Time"] = "Yangon (Rangoon)", ["E. Africa Standard Time"] = "Nairobi",
    ["Hawaiian Standard Time"] = "Hawaii", ["E. Europe Standard Time"] = "E. Europe", ["Tokyo Standard Time"] = "Osaka, Sapporo, Tokyo",
    ["Egypt Standard Time"] = "Cairo", ["SA Eastern Standard Time"] = "Cayenne, Fortaleza", ["GMT Standard Time"] = "Dublin, Edinburgh, Lisbon, London",
    ["Fiji Standard Time"] = "Fiji", ["West Asia Standard Time"] = "Ashgabat, Tashkent", ["Georgian Standard Time"] = "Tbilisi", ["GTB Standard Time"] = "Athens, Bucharest",
    ["Greenland Standard Time"] = "Greenland", ["West Pacific Standard Time"] = "Guam, Port Moresby", ["Mauritius Standard Time"] = "Port Louis",
    ["India Standard Time"] = "Chennai, Kolkata, Mumbai, New Delhi", ["Iran Standard Time"] = "Tehran", ["Arabic Standard Time"] = "Baghdad",
    ["Israel Standard Time"] = "Jerusalem", ["Jordan Standard Time"] = "Amman", ["UTC+12"] = "Coordinated Universal Time+12", ["Korea Standard Time"] = "Seoul",
    ["Middle East Standard Time"] = "Beirut", ["Central Standard Time (Mexico)"] = "Guadalajara, Mexico City, Monterrey", ["Ulaanbaatar Standard Time"] = "Ulaanbaatar",
    ["Morocco Standard Time"] = "Casablanca", ["Namibia Standard Time"] = "Windhoek", ["Nepal Standard Time"] = "Kathmandu",
    ["Central Pacific Standard Time"] = "Solomon Is., New Caledonia", ["New Zealand Standard Time"] = "Auckland, Wellington", ["Arabian Standard Time"] = "Abu Dhabi, Muscat",
    ["Pakistan Standard Time"] = "Islamabad, Karachi", ["Paraguay Standard Time"] = "Asuncion", ["Pacific Standard Time"] = "Pacific Time (US & Canada)",
    ["Russian Standard Time"] = "Moscow, St. Petersburg", ["Samoa Standard Time"] = "Samoa", ["UTC-02"] = "Coordinated Universal Time-02",
    ["Sri Lanka Standard Time"] = "Sri Jayawardenepura", ["Syria Standard Time"] = "Damascus", ["Taipei Standard Time"] = "Taipei", ["Tonga Standard Time"] = "Nuku'alofa",
    ["Turkey Standard Time"] = "Istanbul", ["Mountain Standard Time"] = "Mountain Time (US & Canada)", ["Montevideo Standard Time"] = "Montevideo",
    ["Venezuela Standard Time"] = "Caracas",
    ["Central Standard Time"] = "Central Time (US & Canada)", ["Alaskan Standard Time"] = "Alaska", ["US Mountain Standard Time"] = "Arizona",
    ["Canada Central Standard Time"] = "Saskatchewan", ["Newfoundland Standard Time"] = "Newfoundland", ["AUS Central Standard Time"] = "Darwin",
    ["Cen. Australia Standard Time"] = "Adelaide", ["E. Australia Standard Time"] = "Brisbane", ["Tasmania Standard Time"] = "Hobart",
    ["W. Australia Standard Time"] = "Perth",
};

// Languages Outlook offers widely; each one's native name and short date/time patterns come from CLDR at generation time.
string[] locales =
[
    "af-ZA", "ar-SA", "bg-BG", "ca-ES", "cs-CZ", "cy-GB", "da-DK", "de-AT", "de-CH", "de-DE", "el-GR", "en-AU", "en-CA", "en-GB", "en-IE", "en-IN",
    "en-NZ", "en-US", "en-ZA", "es-ES", "es-MX", "et-EE", "eu-ES", "fi-FI", "fil-PH", "fr-BE", "fr-CA", "fr-CH", "fr-FR", "ga-IE", "gl-ES", "he-IL",
    "hi-IN", "hr-HR", "hu-HU", "id-ID", "is-IS", "it-IT", "ja-JP", "kk-KZ", "ko-KR", "lt-LT", "lv-LV", "ms-MY", "nb-NO", "nl-BE", "nl-NL", "pl-PL",
    "pt-BR", "pt-PT", "ro-RO", "ru-RU", "sk-SK", "sl-SI", "sr-Latn-RS", "sv-SE", "th-TH", "tr-TR", "uk-UA", "vi-VN", "zh-CN", "zh-HK", "zh-TW",
];

var windowsIds = new SortedSet<string>(StringComparer.Ordinal);
foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
{
    if (TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows))
    {
        windowsIds.Add(windows);
    }
}

var zones = new List<(string Id, TimeSpan Offset, string Display, string Iana)>();
foreach (var id in windowsIds)
{
    if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) || !TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var info))
    {
        continue;
    }

    var offset = info.BaseUtcOffset;
    var prefix = offset == TimeSpan.Zero ? "(UTC)" : $"(UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)})";
    zones.Add((id, offset, $"{prefix} {descriptions.GetValueOrDefault(id, id)}", iana));
}

zones.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : string.CompareOrdinal(a.Display, b.Display));

static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

var output = new StringBuilder();
output.AppendLine("// <auto-generated>tools/outlook-catalog/generate.cs; do not edit by hand.</auto-generated>");
output.AppendLine("namespace Nightware.Application.MailboxSettings;");
output.AppendLine();
output.AppendLine("public static partial class OutlookCatalog");
output.AppendLine("{");
output.AppendLine("    /// <summary>Windows id, display name and the IANA id it maps to, by standard offset.</summary>");
output.AppendLine("    public static readonly IReadOnlyList<SupportedTimeZone> TimeZones =");
output.AppendLine("    [");
foreach (var z in zones)
{
    output.AppendLine(CultureInfo.InvariantCulture, $"        new({Q(z.Id)}, {Q(z.Display)}, {Q(z.Iana)}),");
}

output.AppendLine("    ];");
output.AppendLine();
output.AppendLine("    /// <summary>Locale, native name, and its short date and time patterns.</summary>");
output.AppendLine("    public static readonly IReadOnlyList<SupportedLanguage> Languages =");
output.AppendLine("    [");
foreach (var name in locales)
{
    var culture = CultureInfo.GetCultureInfo(name);
    // Newer CLDR puts a narrow no-break space before "tt"; Outlook's patterns use a plain one.
    static string Plain(string pattern) => pattern.Replace('\u202F', ' ').Replace('\u00A0', ' ');
    output.AppendLine(CultureInfo.InvariantCulture, $"        new({Q(name)}, {Q(culture.NativeName)}, {Q(Plain(culture.DateTimeFormat.ShortDatePattern))}, {Q(Plain(culture.DateTimeFormat.ShortTimePattern))}),");
}

output.AppendLine("    ];");
output.AppendLine("}");

var target = Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "tools/outlook-catalog", "..", "..", "src", "Nightware.Application", "MailboxSettings", "OutlookCatalog.Data.cs");
File.WriteAllText(target, output.ToString());
Console.WriteLine($"wrote {Path.GetFullPath(target)}: {zones.Count} time zones, {locales.Length} languages");

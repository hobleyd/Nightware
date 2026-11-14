using System.Globalization;
using System.Reflection;

using Microsoft.AspNetCore.Http;

using Nightware.Application.Common.Time;
using Nightware.Domain.Mail;

namespace Nightware.Api.Graph;

/// <summary>
/// The Graph <c>Prefer</c> preferences the façade understands. Bind it as an endpoint
/// parameter; call <see cref="MarkApplied"/> for each preference the response honours so
/// the <c>Preference-Applied</c> header says so (Graph does the same).
/// </summary>
/// <remarks>
/// NightMail sends only <c>outlook.timezone="UTC"</c> and <c>outlook.body-content-type="text"</c>;
/// <c>odata.maxpagesize</c> and <c>return=minimal</c> are parsed for completeness. Unknown
/// preferences and malformed values are ignored, as RFC 7240 requires.
/// </remarks>
public sealed record PreferHeader
{
    public const string HeaderName = "Prefer";
    public const string AppliedHeaderName = "Preference-Applied";
    public const string TimeZoneKey = "outlook.timezone";
    public const string BodyContentTypeKey = "outlook.body-content-type";
    public const string MaxPageSizeKey = "odata.maxpagesize";
    public const string ReturnKey = "return";

    public static PreferHeader None { get; } = new();

    /// <summary>The zone name exactly as sent (Windows or IANA id), when it resolves to a known zone.</summary>
    public string? TimeZoneName { get; init; }

    /// <summary>The zone to express <c>dateTimeTimeZone</c> values in; UTC when not specified.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The zone label to write in <c>dateTimeTimeZone.timeZone</c>.</summary>
    public string TimeZoneLabel => TimeZoneName ?? GraphTimeZone.Utc;

    public BodyType? BodyContentType { get; init; }

    public int? MaxPageSize { get; init; }

    public bool ReturnMinimal { get; init; }

    /// <summary>Minimal-API binding hook.</summary>
    public static ValueTask<PreferHeader?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult<PreferHeader?>(Parse(context.Request.Headers[HeaderName]));
    }

    /// <summary>Parses one or more <c>Prefer</c> header values (each may hold comma-separated preferences).</summary>
    public static PreferHeader Parse(IEnumerable<string?> headerValues)
    {
        ArgumentNullException.ThrowIfNull(headerValues);
        var result = new PreferHeader();
        foreach (var (key, value) in Tokens(headerValues))
        {
            result = key switch
            {
                TimeZoneKey when GraphTimeZone.TryFind(value, out var zone) => result with { TimeZoneName = value, TimeZone = zone },
                BodyContentTypeKey when string.Equals(value, "text", StringComparison.OrdinalIgnoreCase) => result with { BodyContentType = BodyType.Text },
                BodyContentTypeKey when string.Equals(value, "html", StringComparison.OrdinalIgnoreCase) => result with { BodyContentType = BodyType.Html },
                MaxPageSizeKey when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0 => result with { MaxPageSize = size },
                ReturnKey when string.Equals(value, "minimal", StringComparison.OrdinalIgnoreCase) => result with { ReturnMinimal = true },
                _ => result,
            };
        }

        return result;
    }

    /// <summary>Adds a <c>Preference-Applied</c> entry, e.g. <c>outlook.timezone="UTC"</c>.</summary>
    public static void MarkApplied(HttpResponse response, string key, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var entry = value is null ? key : $"{key}=\"{value}\"";
        var existing = response.Headers[AppliedHeaderName].ToString();
        response.Headers[AppliedHeaderName] = string.IsNullOrEmpty(existing) ? entry : $"{existing}, {entry}";
    }

    /// <summary>Marks the time-zone preference applied when the request sent one.</summary>
    public void MarkTimeZoneApplied(HttpResponse response)
    {
        if (TimeZoneName is not null)
        {
            MarkApplied(response, TimeZoneKey, TimeZoneName);
        }
    }

    /// <summary>Marks the body-content-type preference applied when the request sent one.</summary>
    public void MarkBodyContentTypeApplied(HttpResponse response)
    {
        if (BodyContentType is { } type)
        {
            MarkApplied(response, BodyContentTypeKey, type == BodyType.Text ? "text" : "html");
        }
    }

    private static IEnumerable<(string Key, string? Value)> Tokens(IEnumerable<string?> headerValues)
    {
        foreach (var header in headerValues)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            foreach (var part in SplitOutsideQuotes(header, ','))
            {
                // A preference may carry ;parameters — only the leading name=value matters here.
                var preference = SplitOutsideQuotes(part, ';').FirstOrDefault()?.Trim();
                if (string.IsNullOrEmpty(preference))
                {
                    continue;
                }

                var eq = preference.IndexOf('=', StringComparison.Ordinal);
                var key = (eq < 0 ? preference : preference[..eq]).Trim().ToLowerInvariant();
                var value = eq < 0 ? null : Unquote(preference[(eq + 1)..].Trim());
                yield return (key, value);
            }
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static List<string> SplitOutsideQuotes(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
            }
            else if (text[i] == separator && !quoted)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }
}

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using Nightware.Domain.Calendar;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Admin.Pages;

/// <summary>
/// The automatic-reply form a user's and a shared mailbox's pages share. Messages are plain text here and simple HTML
/// in the store (paragraph breaks kept); times are UTC, as the console states them.
/// </summary>
public sealed partial class AutoReplyForm
{
    /// <summary><c>off</c>, <c>on</c> or <c>scheduled</c>.</summary>
    public string Status { get; set; } = "off";

    /// <summary><c>all</c> (everyone) or <c>internal</c> (the organisation only).</summary>
    public string Audience { get; set; } = "all";

    public string? InternalMessage { get; set; }

    public string? ExternalMessage { get; set; }

    /// <summary>UTC, as an HTML <c>datetime-local</c> value.</summary>
    public string? Start { get; set; }

    public string? End { get; set; }

    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm";

    public static AutoReplyForm From(AutoReplySetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return new AutoReplyForm
        {
            Status = setting.Status switch { AutoReplyStatus.AlwaysEnabled => "on", AutoReplyStatus.Scheduled => "scheduled", _ => "off" },
            Audience = setting.ExternalAudience == ExternalAudience.All ? "all" : "internal",
            InternalMessage = Text(setting.InternalReplyMessage),
            ExternalMessage = Text(setting.ExternalReplyMessage),
            Start = Local(setting.ScheduledStartDateTime),
            End = Local(setting.ScheduledEndDateTime),
        };
    }

    /// <summary>The setting, or the name of the field that is wrong.</summary>
    public (AutoReplySetting? Setting, string? Error) ToSetting()
    {
        var status = Status switch { "on" => AutoReplyStatus.AlwaysEnabled, "scheduled" => AutoReplyStatus.Scheduled, _ => AutoReplyStatus.Disabled };
        DateTimeTimeZone? start = null, end = null;
        if (status == AutoReplyStatus.Scheduled)
        {
            if (!TryUtc(Start, out start) || !TryUtc(End, out end))
            {
                return (null, "A scheduled reply needs a start and an end.");
            }
        }

        if (status != AutoReplyStatus.Disabled && string.IsNullOrWhiteSpace(InternalMessage) && string.IsNullOrWhiteSpace(ExternalMessage))
        {
            return (null, "Write the reply people should get.");
        }

        return (new AutoReplySetting(status)
        {
            ExternalAudience = Audience == "internal" ? ExternalAudience.None : ExternalAudience.All,
            InternalReplyMessage = Html(InternalMessage),
            ExternalReplyMessage = Html(ExternalMessage),
            ScheduledStartDateTime = start,
            ScheduledEndDateTime = end,
        }, null);
    }

    /// <summary>Plain text as HTML: escaped, a blank line a paragraph, a line break a &lt;br&gt;.</summary>
    internal static string Html(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(paragraphs.Select(static p => "<p>" + WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>", StringComparison.Ordinal) + "</p>"));
    }

    /// <summary>The stored HTML as plain text for the textarea.</summary>
    internal static string Text(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = BreakTag().Replace(html, "\n");
        text = ParagraphEnd().Replace(text, "\n\n");
        text = AnyTag().Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static string? Local(DateTimeTimeZone? value) =>
        value is null ? null : Application.Common.Time.GraphTimeZone.ToInstant(value.DateTime, value.TimeZone).UtcDateTime.ToString(LocalFormat, CultureInfo.InvariantCulture);

    private static bool TryUtc(string? value, out DateTimeTimeZone? result)
    {
        result = null;
        if (!DateTime.TryParseExact(value, [LocalFormat, LocalFormat + ":ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        result = new DateTimeTimeZone(parsed, "UTC");
        return true;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"</p\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphEnd();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();
}

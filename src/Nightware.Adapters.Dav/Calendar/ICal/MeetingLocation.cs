using Nightware.Adapters.Dav.Talk;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>
/// The Talk join link an online event carries in <c>LOCATION</c> as well as <c>URL</c> (ADR 0009 D7, 2026-11-23).
/// Talk's calendar integration renames and re-times a room bound to an event whenever the room owner's event whose
/// <c>LOCATION</c> holds <c>…/call/{token}</c> changes, taking the token from the last path segment; so the link goes
/// after any physical location (<c>Boardroom, {link}</c>). Graph's <c>location</c> never shows it: reading strips the
/// event's own link again. Only a <c>URL</c> shaped like a Talk link (<c>…/call/{token}</c>) takes part, so any other
/// event reads and writes its <c>LOCATION</c> exactly as before.
/// </summary>
internal static class MeetingLocation
{
    private const string Separator = ", ";

    /// <summary>The event's Talk join link (its <c>URL</c>); null when it has none.</summary>
    public static string? LinkOf(IcalEvent vevent)
    {
        ArgumentNullException.ThrowIfNull(vevent);
        return IsCallLink(vevent.Url) ? vevent.Url!.OriginalString.Trim() : null;
    }

    /// <summary>The join link of a domain event's meeting URL, when it is a Talk link; otherwise null.</summary>
    public static string? LinkOf(string? onlineMeetingUrl) =>
        onlineMeetingUrl is not null && Uri.TryCreate(onlineMeetingUrl.Trim(), UriKind.Absolute, out var url) && IsCallLink(url) ? onlineMeetingUrl.Trim() : null;

    /// <summary>True for an http(s) link without query or fragment whose path ends <c>call/{token}</c>.</summary>
    public static bool IsCallLink(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            return false;
        }

        var segments = url.AbsolutePath.TrimEnd('/').Split('/');
        return segments.Length >= 2 && segments[^2] == "call" && TalkRooms.IsToken(segments[^1]);
    }

    /// <summary>The location as Graph shows it: without a trailing <paramref name="link"/> (and its separator); null when nothing else is left.</summary>
    public static string? Strip(string? location, string? link)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var text = location.Trim();
        if (link is not null && text.EndsWith(link, StringComparison.Ordinal))
        {
            var before = text[..^link.Length];
            if (before.Length == 0 || char.IsWhiteSpace(before[^1]) || before[^1] is ',' or ';')
            {
                text = before.TrimEnd().TrimEnd(',', ';').TrimEnd();
            }
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>The stored <c>LOCATION</c>: the physical location (if any) followed by the link, which Talk reads as the last path segment.</summary>
    public static string With(string? location, string link)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);
        return Strip(location, link) is { } place ? place + Separator + link : link;
    }
}

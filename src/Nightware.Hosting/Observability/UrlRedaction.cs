using System.Text.RegularExpressions;

namespace Nightware.Hosting.Observability;

/// <summary>
/// The path segments that are secrets although paths are otherwise logged: a Graph share id
/// (<c>/shares/u!{base64url of the sharing URL}</c> — a Nextcloud public link is readable by anyone holding
/// it) and Nextcloud's public-share WebDAV token (<c>/public.php/dav/files/{token}</c>). Each is replaced by
/// <see cref="Placeholder"/>, wherever in a path, URL or formatted log text it appears.
/// </summary>
public static partial class UrlRedaction
{
    public const string Placeholder = "{redacted}";

    /// <summary><paramref name="text"/> with every secret segment replaced; the same instance when there is none.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MayContainSecret(text) ? SecretSegment().Replace(text, Placeholder) : text;
    }

    /// <summary>A URL as HttpClient's own logging would print it — query replaced by <c>*</c> — with secret segments redacted.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            return Redact(uri.OriginalString);
        }

        var query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?*";
        return Redact(uri.GetLeftPart(UriPartial.Path)) + query;
    }

    // A cheap pre-check: almost no log text names either segment.
    private static bool MayContainSecret(string text) =>
        text.Contains("/shares/", StringComparison.OrdinalIgnoreCase) || text.Contains("/public.php/", StringComparison.OrdinalIgnoreCase);

    // After /public.php/dav/files/: the token segment. After /shares/: everything up to /driveItem, not just
    // one segment — a share id sent as standard base64 can hold a literal '/', and the token is its tail.
    // Both stop at a query, fragment, whitespace or quote (the end of a path inside formatted text).
    [GeneratedRegex(@"(?<=/public\.php/dav/files/)(?!\{redacted\})[^/?#\s'""]+|(?<=/shares/)(?!\{redacted\})(?:(?!/driveItem(?:[/?#\s'""]|$))[^?#\s'""])+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegment();
}

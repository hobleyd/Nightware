using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Core;

/// <summary>
/// Converts between the href forms Sabre emits (server-absolute paths such as
/// <c>/nextcloud/remote.php/dav/calendars/alice/personal/</c>), hrefs relative to
/// the DAV root (<c>calendars/alice/personal/</c>, the form <see cref="DavItemId"/>
/// encodes) and request URIs. Every request URI is confined to the DAV root.
/// </summary>
public sealed class DavPaths
{
    public DavPaths(IOptions<DavOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        var baseUrl = value.BaseUrl ?? throw new InvalidOperationException("Dav:BaseUrl is not configured.");
        Origin = new Uri(baseUrl.GetLeftPart(UriPartial.Authority) + "/");
        var basePath = baseUrl.AbsolutePath.TrimEnd('/');
        RootPath = $"{basePath}/{value.DavRoot.Trim('/')}/";
        OcsRootPath = $"{basePath}/{value.OcsRoot.Trim('/')}/";
    }

    /// <summary>Scheme, host and port of Nextcloud, with a trailing slash.</summary>
    public Uri Origin { get; }

    /// <summary>Server-absolute path of the DAV root, with a trailing slash (e.g. <c>/remote.php/dav/</c>).</summary>
    public string RootPath { get; }

    /// <summary>Server-absolute path of the OCS root, with a trailing slash.</summary>
    public string OcsRootPath { get; }

    /// <summary>The absolute URL of the DAV root.</summary>
    public Uri RootUri => new(Origin, RootPath);

    /// <summary>
    /// Server-absolute path for an href that is either server-absolute (<c>/remote.php/dav/…</c>),
    /// a full URL on the Nextcloud origin, or relative to the DAV root. Null when it escapes the root.
    /// </summary>
    public string? ToAbsolutePath(string href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        string path;
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (href.StartsWith('/'))
        {
            path = href;
        }
        else if (Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                 && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (!string.Equals(absolute.GetLeftPart(UriPartial.Authority) + "/", Origin.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            path = absolute.AbsolutePath;
        }
        else
        {
            if (!DavHrefRules.IsSafeRelative(href))
            {
                return null;
            }

            path = RootPath + href;
        }

        if (!path.StartsWith(RootPath, StringComparison.Ordinal)
            && !string.Equals(path, RootPath.TrimEnd('/'), StringComparison.Ordinal))
        {
            return null;
        }

        var relative = path.Length > RootPath.Length ? path[RootPath.Length..] : string.Empty;
        return relative.Length == 0 || DavHrefRules.IsSafeRelative(relative) ? path : null;
    }

    /// <summary>The href relative to the DAV root, or null when it is outside the root or unsafe.</summary>
    public string? ToRelative(string href)
    {
        var absolute = ToAbsolutePath(href);
        if (absolute is null || absolute.Length <= RootPath.Length)
        {
            return null;
        }

        return absolute[RootPath.Length..];
    }

    /// <summary>Request URI for an href (see <see cref="ToAbsolutePath"/>); null when it escapes the DAV root.</summary>
    public Uri? ToRequestUri(string href)
    {
        var path = ToAbsolutePath(href);
        return path is null ? null : new Uri(Origin, path);
    }
}

/// <summary>Shape rules for hrefs relative to the DAV root.</summary>
internal static class DavHrefRules
{
    public const int MaxLength = 2048;

    /// <summary>
    /// True for a non-empty relative path with no scheme, authority, query, fragment,
    /// backslash, control character, empty segment or dot segment — checked both as
    /// written and after percent-decoding.
    /// </summary>
    public static bool IsSafeRelative(string href)
    {
        if (string.IsNullOrEmpty(href) || href.Length > MaxLength || href.StartsWith('/'))
        {
            return false;
        }

        if (!HasSafeShape(href))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(href);
        }
        catch (UriFormatException)
        {
            return false;
        }

        // A decoded '/' is fine inside a segment name; decoded dot segments and control chars are not.
        return decoded == href || HasSafeShape(decoded);
    }

    private static bool HasSafeShape(string path)
    {
        if (path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) && path.IndexOf(':', StringComparison.Ordinal) < FirstSlash(path))
        {
            return false;
        }

        foreach (var c in path)
        {
            if (c is '\\' or '?' or '#' || char.IsControl(c))
            {
                return false;
            }
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isTrailing = i == segments.Length - 1;
            if (segment.Length == 0 && !isTrailing)
            {
                return false;
            }

            if (segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static int FirstSlash(string path)
    {
        var index = path.IndexOf('/', StringComparison.Ordinal);
        return index < 0 ? path.Length : index;
    }
}

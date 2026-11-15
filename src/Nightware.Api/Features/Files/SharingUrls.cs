using System.Buffers.Text;
using System.Text;

using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files;

/// <summary>
/// Graph's sharing-URL share ids (<c>u!</c> + unpadded base64url of the URL) and the check that the link
/// lies on one of this server's file sites (<c>Discovery:FilesBaseUrls</c>, the list NightMail is told).
/// </summary>
/// <remarks>
/// The site check is what keeps the façade from being pointed anywhere: the backend only ever sees the path
/// below the site, and talks to its own configured Nextcloud. A link elsewhere is 404 — Graph's answer for a
/// share it does not know — and so is everything when no file site is configured (the feature is off).
/// </remarks>
internal static class SharingUrls
{
    private const string Prefix = "u!";
    private const int MaxShareIdLength = 4096;

    public static Result<ShareLink> Resolve(string shareId, IEnumerable<string>? fileSites)
    {
        ArgumentNullException.ThrowIfNull(shareId);
        if (!TryDecode(shareId, out var url))
        {
            return Error.Validation("The share id is not an encoded sharing URL ('u!' + base64url).", "shareId");
        }

        foreach (var site in fileSites ?? [])
        {
            if (Uri.TryCreate(site, UriKind.Absolute, out var siteUri) && TryRelativeTo(siteUri, url, out var sitePath))
            {
                return new ShareLink(url, sitePath);
            }
        }

        return Error.NotFound("The sharing link is not on a file site of this server.");
    }

    /// <summary>The absolute http(s) URL a <c>u!</c> share id encodes. Padding and standard base64 are tolerated.</summary>
    internal static bool TryDecode(string shareId, out Uri url)
    {
        url = null!;
        if (!shareId.StartsWith(Prefix, StringComparison.Ordinal) || shareId.Length > MaxShareIdLength)
        {
            return false;
        }

        var encoded = shareId[Prefix.Length..].TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (encoded.Length == 0 || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(Base64Url.DecodeFromChars(encoded));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>
    /// The path of <paramref name="url"/> below <paramref name="site"/> (no leading slash, still escaped), when it
    /// has the site's scheme, host and port and its path starts with the site's path on a segment boundary.
    /// </summary>
    internal static bool TryRelativeTo(Uri site, Uri url, out string sitePath)
    {
        sitePath = string.Empty;
        if (!string.Equals(site.Scheme, url.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(site.IdnHost, url.IdnHost, StringComparison.OrdinalIgnoreCase)
            || site.Port != url.Port)
        {
            return false;
        }

        var basePath = site.AbsolutePath.TrimEnd('/') + "/";
        var path = url.AbsolutePath;
        if (!path.StartsWith(basePath, StringComparison.Ordinal))
        {
            return false;
        }

        sitePath = path[basePath.Length..];
        return sitePath.Length > 0;
    }
}

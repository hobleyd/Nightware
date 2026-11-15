using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Dav.Files;

/// <summary>The two Nextcloud link shapes a document preview accepts.</summary>
internal abstract partial record NextcloudLink
{
    /// <summary><c>{site}/s/{token}</c>: a public share, readable without an account.</summary>
    public sealed record PublicShare(string Token) : NextcloudLink;

    /// <summary><c>{site}/f/{fileId}</c>: Nextcloud's internal link, readable only by someone the file is shared with.</summary>
    public sealed record InternalFile(long FileId) : NextcloudLink;

    /// <summary>
    /// Reads a link's path below its site (see <c>ShareLink.SitePath</c>). Accepts the pretty form and the
    /// <c>index.php/</c> form Nextcloud emits without URL rewriting, with or without a trailing slash; anything
    /// else — a share's sub-path, <c>/download</c>, the Files app — is not a document link.
    /// </summary>
    public static bool TryParse(string? sitePath, [NotNullWhen(true)] out NextcloudLink? link)
    {
        link = null;
        var match = sitePath is null ? Match.Empty : LinkPattern().Match(sitePath);
        if (!match.Success)
        {
            return false;
        }

        if (match.Groups["token"].Success)
        {
            link = new PublicShare(match.Groups["token"].Value);
            return true;
        }

        if (long.TryParse(match.Groups["id"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            link = new InternalFile(id);
            return true;
        }

        return false;
    }

    // Share tokens are generated from [A-Za-z0-9]; Nextcloud also accepts custom tokens with - _ and +.
    [GeneratedRegex(@"^(?:index\.php/)?(?:s/(?<token>[A-Za-z0-9_+-]{1,128})|f/(?<id>[0-9]{1,18}))/?$", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}

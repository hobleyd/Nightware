using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph;
using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Download;

/// <summary>
/// The short-lived URL <c>/shares/{id}/driveItem/content</c> redirects to (OData's raw-value segment, so the
/// request gets the transfer budget like every other <c>$value</c> route):
/// <c>{version root}/shares/{shareId}/driveItem/$value[?format=pdf]&amp;authtoken=…</c>. NightMail fetches
/// it with no <c>Authorization</c> header, as it does a Graph pre-authenticated download URL, so the sealed
/// credential travels in <see cref="AuthTokenParameter"/> — the query, never the path, which is logged and
/// span-tagged while queries are redacted.
/// </summary>
internal static class DownloadUrl
{
    public const string AuthTokenParameter = "authtoken";
    public const string FormatParameter = "format";
    public const string RoutePattern = "/shares/{shareId}/driveItem/$value";

    /// <summary>How long a download URL works: the client follows it at once.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Kestrel's default request-line limit; a download URL must stay well inside it.</summary>
    public const int MaxRequestLine = 8192;

    public static string Build(string versionRoot, DownloadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var format = binding.Format == ContentFormat.Pdf ? $"{FormatParameter}=pdf&" : string.Empty;
        return $"{versionRoot}/shares/{Uri.EscapeDataString(binding.ShareId)}/driveItem/$value?{format}{AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    /// <summary>The share id named by a request path, when it is a download URL path under a Graph version root.</summary>
    public static bool TryParse(PathString path, [NotNullWhen(true)] out string? shareId)
    {
        shareId = null;
        var version = GraphPaths.VersionOf(path);
        if (!path.StartsWithSegments(version, StringComparison.OrdinalIgnoreCase, out var rest) || rest.Value is not { } value
            || value.Split('/') is not ["", var shares, var id, var driveItem, var raw]
            || !Is(shares, "shares") || !Is(driveItem, "driveItem") || raw != "$value" || id.Length == 0)
        {
            return false;
        }

        shareId = id;
        return true;
    }

    /// <summary>Graph's <c>format</c> query option: absent means the original; only <c>pdf</c> is supported.</summary>
    public static bool TryParseFormat(string? value, out ContentFormat format)
    {
        format = ContentFormat.Original;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (string.Equals(value, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            format = ContentFormat.Pdf;
            return true;
        }

        return false;
    }

    private static bool Is(string segment, string expected) => string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);
}

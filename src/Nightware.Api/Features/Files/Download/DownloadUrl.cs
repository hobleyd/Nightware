using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;
using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Download;

/// <summary>What a download URL is bound to: <c>GET</c> of one share id in one format.</summary>
internal sealed record DownloadUrlBinding(string ShareId, ContentFormat Format)
{
    public string[] BoundTo => [ShareId, Format == ContentFormat.Pdf ? "pdf" : string.Empty];
}

/// <summary>
/// The short-lived URL <c>/shares/{id}/driveItem/content</c> redirects to (OData's raw-value segment, so the
/// request gets the transfer budget like every other <c>$value</c> route):
/// <c>{version root}/shares/{shareId}/driveItem/$value[?format=pdf]&amp;authtoken=…</c>. NightMail fetches
/// it with no <c>Authorization</c> header, as it does a Graph pre-authenticated download URL, so the sealed
/// credential (<see cref="Kind"/>) travels in the query. The path's share id is redacted from logs and spans.
/// </summary>
internal static class DownloadUrl
{
    public const string FormatParameter = "format";
    public const string RoutePattern = "/shares/{shareId}/driveItem/$value";

    /// <summary>
    /// A download URL works for five minutes (the client follows it at once). A bearer may use the route too:
    /// it grants nothing <c>/content</c> would not.
    /// </summary>
    public static readonly SelfAuthorisingUrl Kind = new("nightware/share-download-url/v1", HttpMethods.Get, TimeSpan.FromMinutes(5), BindingOf);

    public static string Build(string versionRoot, DownloadUrlBinding binding, string sealedCredential)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var format = binding.Format == ContentFormat.Pdf ? $"{FormatParameter}=pdf&" : string.Empty;
        return $"{versionRoot}/shares/{Uri.EscapeDataString(binding.ShareId)}/driveItem/$value?{format}{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";
    }

    /// <summary>Graph's <c>format</c> query option, once at most: absent means the original; only <c>pdf</c> is supported.</summary>
    public static bool TryParseFormat(IQueryCollection query, out ContentFormat format)
    {
        ArgumentNullException.ThrowIfNull(query);
        var values = query[FormatParameter];
        format = ContentFormat.Original;
        return values.Count <= 1 && TryParseFormat(values.ToString(), out format);
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

    private static string[]? BindingOf(HttpRequest request) =>
        SelfAuthorisingUrl.RouteValue(request, "shareId") is { } shareId && TryParseFormat(request.Query, out var format)
            ? new DownloadUrlBinding(shareId, format).BoundTo
            : null;
}

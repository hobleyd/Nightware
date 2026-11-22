using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;
using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// The drive's self-authorising URLs (ADR 0001 §39, ADR 0008 D3/D4/D6): fetched without an <c>Authorization</c> header, as
/// Graph's pre-authenticated URLs are, so the sealed credential travels in the query and is bound to the path.
/// </summary>
internal static class DriveUrls
{
    /// <summary><c>GET …/content</c> redirects here: <c>{root}/me/drive/items/{id}/$value?authtoken=…</c>, for five minutes.</summary>
    public const string DownloadPattern = "/me/drive/items/{itemId}/$value";

    /// <summary>A thumbnail's <c>url</c>: <c>{root}/me/drive/items/{id}/thumbnails/0/{size}/$value?authtoken=…</c>, for an hour.</summary>
    public const string ThumbnailPattern = "/me/drive/items/{itemId}/thumbnails/0/{size}/$value";

    /// <summary>An upload session's <c>uploadUrl</c>: PUT a chunk, GET the progress, DELETE to cancel.</summary>
    public const string UploadPattern = "/me/drive/uploadSessions/{sessionId}";

    /// <summary>A copy's monitor (<c>Location</c> of the 202): reports the finished copy.</summary>
    public const string MonitorPattern = "/me/drive/copyMonitors/{itemId}";

    public static readonly SelfAuthorisingUrl Download = new("nightware/drive-download-url/v1", HttpMethods.Get, TimeSpan.FromMinutes(5), r => Bound(r, "itemId"));

    public static readonly SelfAuthorisingUrl Thumbnail = new("nightware/drive-thumbnail-url/v1", HttpMethods.Get, TimeSpan.FromHours(1), ThumbnailBinding);

    public static readonly SelfAuthorisingUrl Upload = new(
        "nightware/drive-upload-url/v1", $"{HttpMethods.Put},{HttpMethods.Get},{HttpMethods.Delete}", TimeSpan.FromHours(24), r => Bound(r, "sessionId"), urlOnly: true);

    public static readonly SelfAuthorisingUrl Monitor = new("nightware/drive-copy-monitor/v1", HttpMethods.Get, TimeSpan.FromHours(1), r => Bound(r, "itemId"));

    public static string DownloadUrl(string versionRoot, string itemId, string sealedCredential) =>
        $"{versionRoot}/me/drive/items/{Uri.EscapeDataString(itemId)}/$value?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";

    public static string ThumbnailUrl(string versionRoot, string itemId, ThumbnailSize size, string sealedCredential) =>
        $"{versionRoot}/me/drive/items/{Uri.EscapeDataString(itemId)}/thumbnails/0/{SizeName(size)}/$value?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";

    public static string UploadUrl(string versionRoot, string sessionId, string sealedCredential) =>
        $"{versionRoot}/me/drive/uploadSessions/{Uri.EscapeDataString(sessionId)}?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";

    public static string MonitorUrl(string versionRoot, string itemId, string sealedCredential) =>
        $"{versionRoot}/me/drive/copyMonitors/{Uri.EscapeDataString(itemId)}?{SelfAuthorisingUrl.AuthTokenParameter}={Uri.EscapeDataString(sealedCredential)}";

    public static string SizeName(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => "small",
        ThumbnailSize.Medium => "medium",
        _ => "large",
    };

    public static bool TryParseSize(string? name, out ThumbnailSize size)
    {
        size = ThumbnailSize.Medium;
        switch (name?.ToLowerInvariant())
        {
            case "small":
                size = ThumbnailSize.Small;
                return true;
            case "medium":
                size = ThumbnailSize.Medium;
                return true;
            case "large":
                size = ThumbnailSize.Large;
                return true;
            default:
                return false;
        }
    }

    private static string[]? Bound(HttpRequest request, string name) =>
        SelfAuthorisingUrl.RouteValue(request, name) is { } value ? [value] : null;

    private static string[]? ThumbnailBinding(HttpRequest request) =>
        SelfAuthorisingUrl.RouteValue(request, "itemId") is { } id && SelfAuthorisingUrl.RouteValue(request, "size") is { } size && TryParseSize(size, out var parsed)
            ? [id, SizeName(parsed)]
            : null;
}

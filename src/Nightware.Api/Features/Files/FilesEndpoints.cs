using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
using Nightware.Api.Discovery;
using Nightware.Api.Features.Files.Download;
using Nightware.Api.Features.Files.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Identity;
using Nightware.Application.Files;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files;

/// <summary>
/// Documents linked from mail (Phase 7): Graph's <c>/shares/{u!url}/driveItem[/content]</c> for links on this
/// server's file sites (<c>Discovery:FilesBaseUrls</c>), as NightMail calls them for SharePoint/OneDrive.
/// </summary>
/// <remarks>
/// <c>/content</c> answers <b>302</b> to a download URL that carries the caller's sealed token (ADR 0001 §39),
/// as Graph redirects to a pre-authenticated URL: NightMail fetches the <c>Location</c> with no
/// <c>Authorization</c> header, on a client that waits minutes rather than seconds. So the slow part — the
/// download, and for <c>format=pdf</c> the Collabora conversion — happens there, and the first leg only checks
/// the item can be served.
/// </remarks>
public static class FilesEndpoints
{
    private const string DriveItem = "/shares/{shareId}/driveItem";

    public static RouteGroupBuilder MapFilesEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapGet(DriveItem, GetItemAsync);
        graph.MapGet(DriveItem + "/content", RedirectToContentAsync);

        // Fetched without a bearer: SelfAuthorisingUrlMiddleware recovers the caller's token from the URL
        // (DownloadUrlCredentialSource) before authentication, so the route is authorised like any other.
        graph.MapGet(DownloadUrl.RoutePattern, DownloadAsync);
        return graph;
    }

    private static string Context(string shareId) => $"shares('{Uri.EscapeDataString(shareId)}')/driveItem";

    private static async Task<IResult> GetItemAsync(
        string shareId, ODataQuery odata, IOptions<DiscoveryOptions> discovery, GetSharedItem useCase, CancellationToken cancellationToken)
    {
        var link = SharingUrls.Resolve(shareId, discovery.Value.FilesBaseUrls);
        return link.IsSuccess
            ? GraphResults.Match(
                await useCase.ExecuteAsync(link.Value, cancellationToken).ConfigureAwait(false),
                item => GraphResults.Entity(DriveItemDto.From(item), Context(shareId), odata.Query.Select))
            : GraphResults.Error(link.Error);
    }

    private static async Task<IResult> RedirectToContentAsync(
        string shareId, HttpRequest request, IOptions<DiscoveryOptions> discovery, PrepareSharedItemDownload useCase, IUserContext caller,
        GraphLinkBuilder links, DownloadUrlProtector protector, CancellationToken cancellationToken)
    {
        var target = Target(shareId, request, discovery.Value);
        if (!target.IsSuccess)
        {
            return GraphResults.Error(target.Error);
        }

        var (link, format) = target.Value;
        var prepared = await useCase.ExecuteAsync(link, format, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsSuccess)
        {
            return GraphResults.Error(prepared.Error);
        }

        var binding = new DownloadUrlBinding(shareId, format);
        var ticket = protector.TicketFor(caller.BearerToken, AccessTokens.ExpiryOf(caller.BearerToken));
        var url = DownloadUrl.Build(links.VersionRoot(request), binding, protector.Seal(ticket, binding));
        return url.Length > DownloadUrl.MaxRequestLine - 64
            // An oversized access token would produce a URL the server refuses to read back.
            ? GraphResults.Error(new Error(ErrorKind.Unknown, "generalException", "The download URL would exceed the request-line limit."))
            : Results.Redirect(url);
    }

    private static async Task<IResult> DownloadAsync(
        string shareId, HttpRequest request, IOptions<DiscoveryOptions> discovery, DownloadSharedItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(shareId, request, discovery.Value);
        if (!target.IsSuccess)
        {
            return GraphResults.Error(target.Error);
        }

        var content = await useCase.ExecuteAsync(target.Value.Link, target.Value.Format, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType, content.Value.FileName);
    }

    private static Result<(ShareLink Link, ContentFormat Format)> Target(string shareId, HttpRequest request, DiscoveryOptions discovery)
    {
        var formats = request.Query[DownloadUrl.FormatParameter];
        if (formats.Count > 1 || !DownloadUrl.TryParseFormat(formats.ToString(), out var format))
        {
            return Error.Validation($"format '{formats}' is not supported; only 'pdf' is.", DownloadUrl.FormatParameter);
        }

        var link = SharingUrls.Resolve(shareId, discovery.FilesBaseUrls);
        return link.IsSuccess ? (link.Value, format) : link.Error;
    }
}

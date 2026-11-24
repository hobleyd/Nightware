using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Auth;
using Nightware.Api.Discovery;
using Nightware.Api.Features.Files.Drives.Dto;
using Nightware.Api.Features.Tasks;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Api.Hosting;
using Nightware.Application.Common.Identity;
using Nightware.Application.Files.Drives;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// Phase 20: the caller's OneDrive over Nextcloud Files (ADR 0008). Every item route is mapped under <c>/me/drive</c>,
/// <c>/users/{x}/drive</c> (the caller only) and <c>/drives/{id}</c>, for <c>root</c>, <c>items/{id}</c> and path
/// addressing (rewritten by <see cref="DrivePathRewriteMiddleware"/> to <c>items/{anchor}/nightware.path/{path}</c>).
/// </summary>
internal static class DriveEndpoints
{
    /// <summary>The three ways to name an item below a drive.</summary>
    private static readonly string[] Items = ["/root", "/items/{itemId}", "/items/{itemId}/" + DrivePathRewriteMiddleware.PathSegment + "/{itemPath}"];

    public static void Map(RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/drive", GetDriveAsync);
            scope.MapGet("/drives", ListDrivesAsync);
            scope.MapGet("/drive/special/{specialName}", GetSpecialAsync);
            MapDrive(scope, "/drive");
        }

        graph.MapGet("/drives/{driveId}", GetDriveAsync);
        MapDrive(graph, "/drives/{driveId}");
        DriveSearchQueryEndpoint.Map(graph);

        // Fetched without a bearer; the sealed URL recovers the caller's token (DriveUrls).
        graph.MapGet(DriveUrls.DownloadPattern, DownloadAsync).AuthorisedBy(DriveUrls.Download);
        graph.MapGet(DriveUrls.ThumbnailPattern, ThumbnailFromUrlAsync).AuthorisedBy(DriveUrls.Thumbnail);
        graph.MapPut(DriveUrls.UploadPattern, UploadChunkAsync).AuthorisedBy(DriveUrls.Upload);
        graph.MapGet(DriveUrls.UploadPattern, UploadStatusAsync).AuthorisedBy(DriveUrls.Upload);
        graph.MapDelete(DriveUrls.UploadPattern, CancelUploadAsync).AuthorisedBy(DriveUrls.Upload);
        graph.MapGet(DriveUrls.MonitorPattern, MonitorAsync).AuthorisedBy(DriveUrls.Monitor);
    }

    private static void MapDrive(IEndpointRouteBuilder group, string drive)
    {
        foreach (var item in Items)
        {
            var at = drive + item;
            group.MapGet(at, GetItemAsync);
            group.MapPatch(at, UpdateAsync);
            group.MapDelete(at, DeleteAsync);
            group.MapGet(at + "/children", ListChildrenAsync);
            group.MapPost(at + "/children", CreateFolderAsync);
            group.MapGet(at + "/content", RedirectToContentAsync);
            group.MapPut(at + "/content", UploadAsync).WithMetadata(LargeUpload.Instance);
            group.MapPost(at + "/copy", CopyAsync);
            group.MapPost(at + "/createUploadSession", BeginUploadAsync);
            group.MapGet(at + "/thumbnails", ThumbnailsAsync);
            group.MapGet(at + "/thumbnails/{setId}/{size}/content", ThumbnailAsync);
            group.MapPost(at + "/createLink", CreateLinkAsync);
            group.MapPost(at + "/invite", InviteAsync);
            group.MapGet(at + "/permissions", PermissionsAsync);
            group.MapPost(at + "/checkout", CheckOutAsync);
            group.MapPost(at + "/checkin", CheckInAsync);
            group.MapGet(at + "/versions", VersionsAsync);
            group.MapGet(at + "/versions/{versionId}", VersionAsync);
            group.MapGet(at + "/versions/{versionId}/content", VersionContentAsync);
            group.MapPost(at + "/versions/{versionId}/restoreVersion", RestoreVersionAsync);
            group.MapDelete(at + "/permissions/{permissionId}", DeletePermissionAsync);
        }

        group.MapGet(drive + "/root/delta", DeltaAsync);
        group.MapGet(drive + "/sharedWithMe", SharedWithMeAsync);
        group.MapGet(drive + "/recent", RecentAsync);
        group.MapGet(drive + "/root/search(q='{q}')", SearchAsync);
        group.MapGet(drive + "/search(q='{q}')", SearchAsync);
    }

    private static async Task<IResult> GetDriveAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata, GetDrive useCase,
        CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(at.DriveId, cancellationToken).ConfigureAwait(false),
            drive => GraphResults.Entity(DriveDto.From(drive, FilesBase(discovery)), at.DriveId is null ? DriveRequests.Context(mailbox, string.Empty) : "drives", odata.Query.Select));
    }

    private static async Task<IResult> ListDrivesAsync(
        Mailbox mailbox, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata, GetDrive useCase, CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(null, cancellationToken).ConfigureAwait(false),
            drive => GraphResults.Collection([DriveDto.From(drive, FilesBase(discovery))], "drives", odata.Query));
    }

    private static async Task<IResult> GetSpecialAsync(
        Mailbox mailbox, string specialName, HttpContext http, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata, GetSpecialFolder useCase,
        CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        // The app folder is named after the OAuth client that asked (azp), as Graph names it after the application.
        var app = http.User.FindFirst("azp")?.Value ?? http.User.FindFirst("client_id")?.Value;
        return DriveRequests.Match(
            await useCase.ExecuteAsync(specialName, app, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Entity(ItemDto(item, discovery), DriveRequests.Context(mailbox, "/special"), odata.Query.Select));
    }

    private static async Task<IResult> GetItemAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer,
        IOptions<DiscoveryOptions> discovery, ODataQuery odata, GetDriveItem useCase, ListDriveItemChildren children, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var item = await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return DriveRequests.Fail(item.Error);
        }

        var dto = ItemDto(item.Value, discovery);
        if (!item.Value.IsFolder && DownloadUrl(request, caller, links, sealer, item.Value.Id) is { } downloadUrl)
        {
            dto = dto with { DownloadUrl = downloadUrl };
        }

        if (odata.Query.ExpandOf("children") is not null)
        {
            if (!item.Value.IsFolder)
            {
                dto = dto with { Children = [] };
            }
            else
            {
                var listed = await children.ExecuteAsync(DriveItemRef.ById(item.Value.Id) with { DriveId = at.DriveId }, new DriveListQuery(DriveLimits.DefaultPageSize, null), cancellationToken)
                    .ConfigureAwait(false);
                if (!listed.IsSuccess)
                {
                    return DriveRequests.Fail(listed.Error);
                }

                dto = dto with { Children = listed.Value.Items.Select(i => ItemDto(i, discovery)).ToList() };
            }
        }

        return GraphResults.Entity(dto, DriveRequests.Context(mailbox, "/items"), odata.Query.Select, odata.Query.Expand);
    }

    private static async Task<IResult> ListChildrenAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller,
        IOptions<DiscoveryOptions> discovery, ODataQuery odata, ListDriveItemChildren useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        var query = DriveRequests.ListQuery(odata.Query);
        if (!target.IsSuccess || !query.IsSuccess)
        {
            return DriveRequests.Fail((target.IsSuccess ? query.Error : target.Error)!);
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(target.Value, query.Value, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, i => ItemDto(i, discovery), DriveRequests.Context(mailbox, "/items"), odata.Query));
    }

    private static async Task<IResult> CreateFolderAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, HttpRequest request,
        IUserContext caller, IOptions<DiscoveryOptions> discovery, CreateDriveFolder useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        if (body is null || !body.ContainsKey("folder"))
        {
            return DriveRequests.Fail(Error.Validation("Only folders are created here: send a 'folder' facet (upload files with PUT …/content).", "folder"));
        }

        var conflict = DriveRequests.Conflict(request, body, ConflictBehavior.Fail);
        if (!conflict.IsSuccess)
        {
            return DriveRequests.Fail(conflict.Error);
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(target.Value, DriveRequests.Text(body, "name"), conflict.Value, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Created(ItemDto(item, discovery), DriveRequests.Context(mailbox, "/items")));
    }

    private static async Task<IResult> RedirectToContentAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller,
        PrepareDriveItemDownload useCase, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var item = await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return DriveRequests.Fail(item.Error);
        }

        return DownloadUrl(request, caller, links, sealer, item.Value.Id) is { } url ? SelfAuthorisingUrl.Redirect(url) : TooLongUrl();
    }

    /// <summary>The five-minute self-authorising download URL for a file; null when it would not fit a request line.</summary>
    private static string? DownloadUrl(HttpRequest request, IUserContext caller, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer, string itemId)
    {
        var ticket = sealer.TicketFor(DriveUrls.Download, caller.BearerToken, request.HttpContext.User);
        var url = DriveUrls.DownloadUrl(links.VersionRoot(request), itemId, sealer.Seal(DriveUrls.Download, ticket, [itemId]));
        return SelfAuthorisingUrl.FitsRequestLine(url) ? url : null;
    }

    private static async Task<IResult> DownloadAsync(string itemId, DownloadDriveItem useCase, CancellationToken cancellationToken)
    {
        var content = await useCase.ExecuteAsync(DriveItemRef.ById(itemId), cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return DriveRequests.Fail(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType, content.Value.FileName);
    }

    private static async Task<IResult> UploadAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller,
        IOptions<DiscoveryOptions> discovery, UploadDriveItemContent useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        var conflict = DriveRequests.Conflict(request, null, ConflictBehavior.Replace);
        if (!target.IsSuccess || !conflict.IsSuccess)
        {
            return DriveRequests.Fail((target.IsSuccess ? conflict.Error : target.Error)!);
        }

        var upload = new DriveUpload(request.Body, request.ContentType ?? "application/octet-stream", request.ContentLength, conflict.Value)
        {
            IfMatch = request.Headers[HeaderNames.IfMatch].ToString() is { Length: > 0 } ifMatch ? ifMatch : null,
        };
        var written = await useCase.ExecuteAsync(target.Value, upload, cancellationToken).ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            return DriveRequests.Fail(written.Error);
        }

        // Graph: 201 for a new file, 200 for new content of an existing one. The store can't tell which it did, so a
        // path-addressed upload (usually a new file) answers 201 and an id-addressed one (always a replacement) 200.
        var dto = ItemDto(written.Value, discovery);
        return target.Value.Path.Count > 0
            ? GraphResults.Created(dto, DriveRequests.Context(mailbox, "/items"))
            : GraphResults.Entity(dto, DriveRequests.Context(mailbox, "/items"));
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, HttpRequest request,
        IUserContext caller, IOptions<DiscoveryOptions> discovery, UpdateDriveItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        var conflict = DriveRequests.Conflict(request, body, ConflictBehavior.Fail);
        var parent = DriveRequests.ParentReference(body);
        if (!target.IsSuccess || !conflict.IsSuccess || !parent.IsSuccess)
        {
            return DriveRequests.Fail((!target.IsSuccess ? target.Error : !conflict.IsSuccess ? conflict.Error : parent.Error)!);
        }

        var change = new DriveItemChange(DriveRequests.Text(body, "name"), parent.Value, conflict.Value)
        {
            IfMatch = request.Headers[HeaderNames.IfMatch].ToString() is { Length: > 0 } ifMatch ? ifMatch : null,
        };
        return DriveRequests.Match(
            await useCase.ExecuteAsync(target.Value, change, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Entity(ItemDto(item, discovery), DriveRequests.Context(mailbox, "/items")));
    }

    private static async Task<IResult> DeleteAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller,
        DeleteDriveItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var ifMatch = request.Headers[HeaderNames.IfMatch].ToString() is { Length: > 0 } value ? value : null;
        var deleted = await useCase.ExecuteAsync(target.Value, ifMatch, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Results.NoContent() : DriveRequests.Fail(deleted.Error);
    }

    private static async Task<IResult> CopyAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, HttpRequest request,
        IUserContext caller, CopyDriveItem useCase, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        var conflict = DriveRequests.Conflict(request, body, ConflictBehavior.Fail);
        var parent = DriveRequests.ParentReference(body);
        if (!target.IsSuccess || !conflict.IsSuccess || !parent.IsSuccess)
        {
            return DriveRequests.Fail((!target.IsSuccess ? target.Error : !conflict.IsSuccess ? conflict.Error : parent.Error)!);
        }

        var copied = await useCase.ExecuteAsync(target.Value, parent.Value, DriveRequests.Text(body, "name"), conflict.Value, cancellationToken).ConfigureAwait(false);
        if (!copied.IsSuccess)
        {
            return DriveRequests.Fail(copied.Error);
        }

        // Done already; still Graph's 202 with a monitor, which reports the copy as completed (ADR 0008 D4).
        var ticket = sealer.TicketFor(DriveUrls.Monitor, caller.BearerToken, request.HttpContext.User);
        var monitor = DriveUrls.MonitorUrl(links.VersionRoot(request), copied.Value.Id, sealer.Seal(DriveUrls.Monitor, ticket, [copied.Value.Id]));
        if (!SelfAuthorisingUrl.FitsRequestLine(monitor))
        {
            return TooLongUrl();
        }

        request.HttpContext.Response.Headers.Location = monitor;
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static IResult MonitorAsync(string itemId) =>
        GraphResults.Entity(new AsyncOperationStatusDto { ResourceId = itemId }, "microsoft.graph.asyncJobStatus");

    private static async Task<IResult> BeginUploadAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, HttpRequest request,
        IUserContext caller, BeginDriveUpload useCase, GetDriveItem items, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var item = body?["item"] as JsonObject;
        var conflict = DriveRequests.Conflict(request, item, ConflictBehavior.Replace);
        if (!conflict.IsSuccess)
        {
            return DriveRequests.Fail(conflict.Error);
        }

        DriveUploadFile file;
        var size = DriveRequests.Number(item, "fileSize") ?? 0;
        if (target.Value.Path.Count > 0)
        {
            // items/{parent}:/{name}:/createUploadSession — the last segment is the new file's name.
            var path = target.Value.Path;
            file = new DriveUploadFile(target.Value with { Path = path.Take(path.Count - 1).ToList() }, path[^1], size, conflict.Value);
        }
        else
        {
            // items/{id}/createUploadSession — new content for an existing file.
            var existing = await items.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false);
            if (!existing.IsSuccess)
            {
                return DriveRequests.Fail(existing.Error);
            }

            if (existing.Value.IsFolder || existing.Value.ParentId is not { } parentId)
            {
                return DriveRequests.Fail(Error.Validation("Upload into a folder with …/items/{folder-id}:/{name}:/createUploadSession."));
            }

            file = new DriveUploadFile(DriveItemRef.ById(parentId) with { DriveId = at.DriveId }, existing.Value.Name, size, ConflictBehavior.Replace);
        }

        var session = await useCase.ExecuteAsync(file, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return DriveRequests.Fail(session.Error);
        }

        var ticket = sealer.TicketFor(DriveUrls.Upload, caller.BearerToken, request.HttpContext.User, session.Value.ExpiresAt);
        var uploadUrl = DriveUrls.UploadUrl(links.VersionRoot(request), session.Value.SessionId, sealer.Seal(DriveUrls.Upload, ticket, [session.Value.SessionId]));
        if (!SelfAuthorisingUrl.FitsRequestLine(uploadUrl))
        {
            return TooLongUrl();
        }

        return GraphResults.Entity(
            new DriveUploadSessionDto { UploadUrl = uploadUrl, ExpirationDateTime = ticket.ExpiresAt, NextExpectedRanges = ["0-"] },
            "microsoft.graph.uploadSession");
    }

    private static async Task<IResult> UploadChunkAsync(
        string sessionId, HttpRequest request, IOptions<DiscoveryOptions> discovery, UploadDriveChunk useCase, CancellationToken cancellationToken)
    {
        if (request.HttpContext.Features.Get<SealedTicket>() is not { } ticket)
        {
            return Results.Challenge();
        }

        var range = TasksEndpoints.ParseContentRange(request.Headers[HeaderNames.ContentRange].ToString());
        if (!range.IsSuccess)
        {
            return DriveRequests.Fail(range.Error);
        }

        if (range.Value is not { } bytes)
        {
            return DriveRequests.Fail(Error.Validation("Each chunk needs a Content-Range.", "Content-Range"));
        }

        var progress = await useCase.ExecuteAsync(sessionId, new ContentRangeHeader(bytes.First, bytes.Last, bytes.Total), request.Body, cancellationToken)
            .ConfigureAwait(false);
        if (!progress.IsSuccess)
        {
            return DriveRequests.Fail(progress.Error);
        }

        // The last chunk answers with the new driveItem (201), as Graph's does.
        return progress.Value.Completed is { } item
            ? GraphResults.Created(ItemDto(item, discovery), "users/drive/items")
            : Results.Json(
                new DriveUploadSessionDto { ExpirationDateTime = ticket.ExpiresAt, NextExpectedRanges = [$"{progress.Value.NextExpected.ToString(CultureInfo.InvariantCulture)}-"] },
                statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> UploadStatusAsync(string sessionId, HttpRequest request, GetDriveUpload useCase, CancellationToken cancellationToken)
    {
        var ticket = request.HttpContext.Features.Get<SealedTicket>();
        return DriveRequests.Match(
            await useCase.ExecuteAsync(sessionId, cancellationToken).ConfigureAwait(false),
            state => GraphResults.Entity(
                new DriveUploadSessionDto
                {
                    ExpirationDateTime = ticket?.ExpiresAt ?? state.ExpiresAt,
                    NextExpectedRanges = [$"{state.NextExpected.ToString(CultureInfo.InvariantCulture)}-"],
                },
                "microsoft.graph.uploadSession"));
    }

    private static async Task<IResult> CancelUploadAsync(string sessionId, CancelDriveUpload useCase, CancellationToken cancellationToken)
    {
        var cancelled = await useCase.ExecuteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return cancelled.IsSuccess ? Results.NoContent() : DriveRequests.Fail(cancelled.Error);
    }

    private static async Task<IResult> ThumbnailsAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller,
        ODataQuery odata, GetDriveItem useCase, GraphLinkBuilder links, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var item = await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return DriveRequests.Fail(item.Error);
        }

        if (!item.Value.HasThumbnail)
        {
            return GraphResults.Collection([], DriveRequests.Context(mailbox, "/items/thumbnails"), odata.Query);
        }

        var root = links.VersionRoot(request);
        var ticket = sealer.TicketFor(DriveUrls.Thumbnail, caller.BearerToken, request.HttpContext.User);
        ThumbnailDto Thumb(ThumbnailSize size)
        {
            var edge = ThumbnailSizes.EdgeOf(size);
            var url = DriveUrls.ThumbnailUrl(root, item.Value.Id, size, sealer.Seal(DriveUrls.Thumbnail, ticket, [item.Value.Id, DriveUrls.SizeName(size)]));
            return new ThumbnailDto { Height = edge, Width = edge, Url = url };
        }

        var set = new ThumbnailSetDto { Small = Thumb(ThumbnailSize.Small), Medium = Thumb(ThumbnailSize.Medium), Large = Thumb(ThumbnailSize.Large) };
        return GraphResults.Collection([set], DriveRequests.Context(mailbox, "/items/thumbnails"), odata.Query);
    }

    private static async Task<IResult> ThumbnailAsync(
        Mailbox mailbox, DriveAddress at, string setId, string size, IUserContext caller,
        OpenDriveThumbnail useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        return setId != "0" || !DriveUrls.TryParseSize(size, out var parsed)
            ? DriveRequests.Fail(Error.NotFound("The item has no such thumbnail."))
            : await StreamThumbnailAsync(useCase, target.Value, parsed, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ThumbnailFromUrlAsync(string itemId, string size, OpenDriveThumbnail useCase, CancellationToken cancellationToken) =>
        DriveUrls.TryParseSize(size, out var parsed)
            ? await StreamThumbnailAsync(useCase, DriveItemRef.ById(itemId), parsed, cancellationToken).ConfigureAwait(false)
            : DriveRequests.Fail(Error.NotFound("The item has no such thumbnail."));

    private static async Task<IResult> StreamThumbnailAsync(OpenDriveThumbnail useCase, DriveItemRef item, ThumbnailSize size, CancellationToken cancellationToken)
    {
        var content = await useCase.ExecuteAsync(item, size, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return DriveRequests.Fail(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType);
    }

    private static async Task<IResult> CreateLinkAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, IUserContext caller,
        CreateSharingLink useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var created = await useCase.ExecuteAsync(
            target.Value, DriveRequests.Text(body, "type"), DriveRequests.Text(body, "scope"), DriveRequests.Instant(body, "expirationDateTime"),
            DriveRequests.Text(body, "password"), cancellationToken).ConfigureAwait(false);
        return DriveRequests.Match(created, share => GraphResults.Created(PermissionDto.From(share, at.DriveId), "microsoft.graph.permission"));
    }

    private static async Task<IResult> InviteAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, IUserContext caller,
        InviteToDriveItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var recipients = (body?["recipients"] as JsonArray ?? [])
            .Select(static r => DriveRequests.Text(r as JsonObject, "email") ?? string.Empty)
            .ToList();
        var roles = (body?["roles"] as JsonArray ?? [])
            .Select(static r => r is JsonValue v && v.TryGetValue<string>(out var s) ? s.ToLowerInvariant() : string.Empty)
            .ToList();
        var invitation = new DriveInvitation(recipients, roles, DriveRequests.Instant(body, "expirationDateTime"));
        return DriveRequests.Match(
            await useCase.ExecuteAsync(target.Value, invitation, cancellationToken).ConfigureAwait(false),
            shares => GraphResults.Collection(shares.Select(s => PermissionDto.From(s, at.DriveId)), "Collection(microsoft.graph.permission)", Application.Common.Query.ItemQuery.Default));
    }

    private static async Task<IResult> PermissionsAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, ODataQuery odata,
        ListDriveItemPermissions useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false),
            shares => GraphResults.Collection(shares.Select(s => PermissionDto.From(s, at.DriveId)), DriveRequests.Context(mailbox, "/items/permissions"), odata.Query));
    }

    private static async Task<IResult> DeletePermissionAsync(
        Mailbox mailbox, DriveAddress at, string permissionId, IUserContext caller,
        RemoveDriveItemShare useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var deleted = await useCase.ExecuteAsync(target.Value, permissionId, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Results.NoContent() : DriveRequests.Fail(deleted.Error);
    }

    private static async Task<IResult> DeltaAsync(
        Mailbox mailbox, DriveAddress at, HttpRequest request, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata,
        DeltaLinkCodec codec, GetDrive drives, GetDriveDelta useCase, CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        if (at.DriveId is not null)
        {
            // /drives/{id}/root/delta: only the caller's own drive has one.
            var drive = await drives.ExecuteAsync(at.DriveId, cancellationToken).ConfigureAwait(false);
            if (!drive.IsSuccess)
            {
                return DriveRequests.Fail(drive.Error);
            }
        }

        var token = request.Query["token"].ToString();
        var latest = string.Equals(token, "latest", StringComparison.OrdinalIgnoreCase);
        if (token.Length > 0 && !latest)
        {
            return DriveRequests.Fail(Error.Validation("token may only be 'latest'; follow the delta link to continue.", "token"));
        }

        var cursor = codec.ReadCursor(request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return DriveRequests.Fail(cursor.Error);
        }

        var page = await useCase.ExecuteAsync(cursor.Value.Token, cursor.Value.Top, latest, cancellationToken).ConfigureAwait(false);
        return DriveRequests.Match(page, p => GraphResults.Delta(p, i => ItemDto(i, discovery), cursor.Value, "driveItem"));
    }

    private static async Task<IResult> SearchAsync(
        Mailbox mailbox, DriveAddress at, string q, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata,
        GetDrive drives, SearchDrive useCase, CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        if (at.DriveId is not null)
        {
            var drive = await drives.ExecuteAsync(at.DriveId, cancellationToken).ConfigureAwait(false);
            if (!drive.IsSuccess)
            {
                return DriveRequests.Fail(drive.Error);
            }
        }

        var query = DriveRequests.ListQuery(odata.Query);
        if (!query.IsSuccess)
        {
            return DriveRequests.Fail(query.Error);
        }

        // OData string literal: a quote is written twice.
        var text = q.Replace("''", "'", StringComparison.Ordinal);
        return DriveRequests.Match(
            await useCase.ExecuteAsync(text, query.Value, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, i => ItemDto(i, discovery), "Collection(driveItem)", odata.Query));
    }

    private static async Task<IResult> CheckOutAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, CheckOutDriveItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var done = await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false);
        return done.IsSuccess ? Results.NoContent() : DriveRequests.Fail(done.Error);
    }

    /// <summary>Graph's <c>checkInAs</c> (SharePoint publishing levels) has no meaning here; only <c>comment</c> is used.</summary>
    private static async Task<IResult> CheckInAsync(
        Mailbox mailbox, DriveAddress at, JsonObject? body, IUserContext caller, CheckInDriveItem useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var done = await useCase.ExecuteAsync(target.Value, DriveRequests.Text(body, "comment"), cancellationToken).ConfigureAwait(false);
        return done.IsSuccess ? Results.NoContent() : DriveRequests.Fail(done.Error);
    }

    private static async Task<IResult> VersionsAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, ODataQuery odata, ListDriveItemVersions useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        return !target.IsSuccess
            ? DriveRequests.Fail(target.Error)
            : DriveRequests.Match(
                await useCase.ExecuteAsync(target.Value, cancellationToken).ConfigureAwait(false),
                versions => GraphResults.Collection(versions.Select(DriveItemVersionDto.From), DriveRequests.Context(mailbox, "/items/versions"), odata.Query));
    }

    private static async Task<IResult> VersionAsync(
        Mailbox mailbox, DriveAddress at, string versionId, IUserContext caller, ODataQuery odata, ListDriveItemVersions useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        return !target.IsSuccess
            ? DriveRequests.Fail(target.Error)
            : DriveRequests.Match(
                await useCase.GetAsync(target.Value, versionId, cancellationToken).ConfigureAwait(false),
                version => GraphResults.Entity(DriveItemVersionDto.From(version), DriveRequests.Context(mailbox, "/items/versions"), odata.Query.Select));
    }

    /// <summary>A version's bytes, streamed (Graph redirects to a download URL; a client following redirects sees the same).</summary>
    private static async Task<IResult> VersionContentAsync(
        Mailbox mailbox, DriveAddress at, string versionId, IUserContext caller, OpenDriveItemVersion useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var content = await useCase.ExecuteAsync(target.Value, versionId, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return DriveRequests.Fail(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType);
    }

    private static async Task<IResult> RestoreVersionAsync(
        Mailbox mailbox, DriveAddress at, string versionId, IUserContext caller, RestoreDriveItemVersion useCase, CancellationToken cancellationToken)
    {
        var target = Target(mailbox, caller, at);
        if (!target.IsSuccess)
        {
            return DriveRequests.Fail(target.Error);
        }

        var restored = await useCase.ExecuteAsync(target.Value, versionId, cancellationToken).ConfigureAwait(false);
        return restored.IsSuccess ? Results.NoContent() : DriveRequests.Fail(restored.Error);
    }

    private static async Task<IResult> SharedWithMeAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata,
        GetDrive drives, ListSharedWithMe useCase, CancellationToken cancellationToken)
    {
        if (await OwnDriveAsync(mailbox, at, caller, drives, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            items => GraphResults.Collection(items.Select(i => RemoteItemDto.Entry(i, FilesBase(discovery))), "Collection(driveItem)", odata.Query));
    }

    private static async Task<IResult> RecentAsync(
        Mailbox mailbox, DriveAddress at, IUserContext caller, IOptions<DiscoveryOptions> discovery, ODataQuery odata,
        GetDrive drives, ListRecentDriveItems useCase, CancellationToken cancellationToken)
    {
        if (await OwnDriveAsync(mailbox, at, caller, drives, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return DriveRequests.Match(
            await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            items => GraphResults.Collection(items.Select(i => RemoteItemDto.Entry(i, FilesBase(discovery))), "Collection(driveItem)", odata.Query));
    }

    /// <summary>Null when the route names the caller's own drive; otherwise the error to answer.</summary>
    private static async Task<IResult?> OwnDriveAsync(Mailbox mailbox, DriveAddress at, IUserContext caller, GetDrive drives, CancellationToken cancellationToken)
    {
        if (DriveRequests.OwnDrive(mailbox, caller) is { } denied)
        {
            return DriveRequests.Fail(denied);
        }

        if (at.DriveId is not null && await drives.ExecuteAsync(at.DriveId, cancellationToken).ConfigureAwait(false) is { IsSuccess: false } missing)
        {
            return DriveRequests.Fail(missing.Error);
        }

        return null;
    }

    private static Result<DriveItemRef> Target(Mailbox mailbox, IUserContext caller, DriveAddress at) =>
        DriveRequests.OwnDrive(mailbox, caller) is { } denied ? denied : DriveRequests.ItemRef(at.ItemId, at.ItemPath, at.DriveId);

    private static DriveItemResourceDto ItemDto(DriveItem item, IOptions<DiscoveryOptions> discovery) =>
        DriveItemResourceDto.From(item, FilesBase(discovery));

    private static string? FilesBase(IOptions<DiscoveryOptions> discovery) => discovery.Value.FilesBaseUrls?.FirstOrDefault();

    private static IResult TooLongUrl() =>
        DriveRequests.Fail(new Error(ErrorKind.Unknown, "generalException", "The URL would exceed the request-line limit."));
}

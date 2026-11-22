using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Read;

/// <summary>
/// The mail read routes (Phase 2), each in its <c>/me</c> and <c>/users/{user}</c> form. GET
/// <c>messages</c> serves the list, the conversation expansion and search; GET <c>messages/{id}</c>
/// the reading pane and the plain-text probe — the use cases tell them apart by the query. Nothing
/// here claims <c>messages/{id}/…</c> beyond attachments and <c>$value</c> (Phase 4 owns <c>…/event</c>).
/// </summary>
public static class MailReadEndpoints
{
    /// <summary>Graph sends the MIME of a message as <c>text/plain</c>; the client reads the bytes and ignores the type.</summary>
    internal const string MimeContentType = "text/plain";

    private const string Attachments = "attachments";

    /// <summary>Maps the mail read routes. Owned by Phase 2.</summary>
    public static RouteGroupBuilder MapMailReadEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/mailFolders", ListFoldersAsync);
            scope.MapGet("/mailFolders/{folderId}", GetFolderAsync);
            scope.MapGet("/mailFolders/{folderId}/childFolders", ListChildFoldersAsync);
            scope.MapGet("/mailFolders/{folderId}/messages", ListFolderMessagesAsync).WithCount();
            scope.MapGet("/mailFolders/{folderId}/messages/$count", ListFolderMessagesAsync).AsCountSegment();
            scope.MapGet("/mailFolders/{folderId}/messages/delta", DeltaAsync);
            scope.MapGet("/mailFolders/delta", FolderDeltaAsync);
            scope.MapGet("/messages", ListMessagesAsync).WithCount();
            scope.MapGet("/messages/$count", ListMessagesAsync).AsCountSegment();
            scope.MapGet("/messages/{messageId}", GetMessageAsync);
            // Graph's folder-scoped item route. Message ids are unique in the mailbox, so the folder is not checked.
            scope.MapGet("/mailFolders/{folderId}/messages/{messageId}", GetMessageAsync);
            foreach (var messages in ItemRoutes.Message)
            {
                scope.MapGet(messages + "/$value", GetMimeAsync);
                scope.MapGet(messages + "/attachments", ListAttachmentsAsync);
                scope.MapGet(messages + "/attachments/{attachmentId}", GetAttachmentAsync);
                scope.MapGet(messages + "/attachments/{attachmentId}/$value", GetAttachmentContentAsync);
            }
        }
        return graph;
    }

    private static async Task<IResult> ListFoldersAsync(Mailbox mailbox, ODataQuery odata, ListMailFolders useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, null, odata.Query, cancellationToken),
            page => GraphResults.Page(page, MessageDtoMapper.ToDto, $"{mailbox.ContextSegment}/mailFolders", odata.Query));

    private static async Task<IResult> GetFolderAsync(Mailbox mailbox, string folderId, ODataQuery odata, GetMailFolder useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, FolderReference.Parse(folderId), cancellationToken),
            folder => GraphResults.Entity(MessageDtoMapper.ToDto(folder), $"{mailbox.ContextSegment}/mailFolders", odata.Query.Select));

    private static async Task<IResult> ListChildFoldersAsync(Mailbox mailbox, string folderId, ODataQuery odata, ListMailFolders useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, FolderReference.Parse(folderId), odata.Query, cancellationToken),
            page => GraphResults.Page(page, MessageDtoMapper.ToDto, $"{mailbox.ContextSegment}/mailFolders('{folderId}')/childFolders", odata.Query));

    private static async Task<IResult> ListFolderMessagesAsync(Mailbox mailbox, string folderId, ODataQuery odata, ListMessages useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, FolderReference.Parse(folderId), odata.Query, cancellationToken),
            page => GraphResults.Page(page, m => ListItem(m, odata.Query), $"{mailbox.ContextSegment}/mailFolders('{folderId}')/messages", odata.Query));

    private static async Task<IResult> ListMessagesAsync(Mailbox mailbox, ODataQuery odata, ListMessages useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, null, odata.Query, cancellationToken),
            page => GraphResults.Page(page, m => ListItem(m, odata.Query), $"{mailbox.ContextSegment}/messages", odata.Query));

    /// <summary>Delta links freeze <c>$select</c>/<c>$top</c> (ADR 0001 §7); the client follows them verbatim.</summary>
    private static async Task<IResult> DeltaAsync(
        Mailbox mailbox, string folderId, ODataQuery odata, HttpRequest request, DeltaLinkCodec links, SyncMessages useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var c = cursor.Value;
        var page = await useCase.ExecuteAsync(
            mailbox.Ref, FolderReference.Parse(folderId), c.Token, new DeltaRequest(c.Top ?? DeltaRequest.DefaultPageSize, c.Select), cancellationToken);
        return GraphResults.Match(page, p => GraphResults.Delta(
            p, m => MessageDtoMapper.ToDto(m, new MessageMappingOptions(c.Select)), c, "message", "#microsoft.graph.message"));
    }

    /// <summary>The folder hierarchy's delta feed (phase 12, ADR 0006 D5).</summary>
    private static async Task<IResult> FolderDeltaAsync(
        Mailbox mailbox, ODataQuery odata, HttpRequest request, DeltaLinkCodec links, SyncFolders useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var c = cursor.Value;
        var page = await useCase.ExecuteAsync(mailbox.Ref, c.Token, new DeltaRequest(c.Top ?? DeltaRequest.DefaultPageSize, c.Select), cancellationToken);
        return GraphResults.Match(page, p => GraphResults.Delta(p, f => MessageDtoMapper.ToDto(f), c, "mailFolder", "#microsoft.graph.mailFolder"));
    }

    private static async Task<IResult> GetMessageAsync(
        Mailbox mailbox, string messageId, ODataQuery odata, PreferHeader prefer, HttpResponse response, GetMessage useCase, CancellationToken cancellationToken)
    {
        var query = odata.Query;
        var fetch = new MessageFetch(query.Select, prefer.BodyContentType, query.Expand.Count > 0 ? query.Expand[0] : null);
        if (query.Expand.Count > 1)
        {
            return GraphResults.Error(Error.Validation("Only $expand=attachments is supported.", "$expand"));
        }

        var view = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), fetch, cancellationToken);
        if (!view.IsSuccess)
        {
            return GraphResults.Error(view.Error);
        }

        prefer.MarkBodyContentTypeApplied(response);
        prefer.MarkTimeZoneApplied(response);
        var options = new MessageMappingOptions(query.Select, query.ExpandOf(Attachments) is not null, prefer, ToDetails(view.Value.Meeting));
        return GraphResults.Entity(MessageDtoMapper.ToDto(view.Value.Message, options), $"{mailbox.ContextSegment}/messages", query.Select, query.Expand);
    }

    private static async Task<IResult> GetMimeAsync(Mailbox mailbox, string messageId, GetMessageMime useCase, CancellationToken cancellationToken) =>
        await StreamAsync(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken), MimeContentType, cancellationToken);

    private static async Task<IResult> ListAttachmentsAsync(Mailbox mailbox, string messageId, ODataQuery odata, ListMessageAttachments useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken),
            attachments => GraphResults.Collection(
                attachments.Select(static a => (object)MessageDtoMapper.ToDto(a)), $"{mailbox.ContextSegment}/messages('{messageId}')/attachments", odata.Query));

    private static async Task<IResult> GetAttachmentAsync(
        Mailbox mailbox, string messageId, string attachmentId, ODataQuery odata, GetMessageAttachment useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), new AttachmentId(attachmentId), cancellationToken),
            attachment => GraphResults.Entity(MessageDtoMapper.ToDto(attachment), $"{mailbox.ContextSegment}/messages('{messageId}')/attachments", odata.Query.Select));

    private static async Task<IResult> GetAttachmentContentAsync(
        Mailbox mailbox, string messageId, string attachmentId, GetMessageAttachmentContent useCase, CancellationToken cancellationToken) =>
        await StreamAsync(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), new AttachmentId(attachmentId), cancellationToken), null, cancellationToken);

    /// <summary>The list projection: the client always <c>$select</c>s, so no meeting facts are needed here.</summary>
    private static MessageDto ListItem(MailMessage message, ItemQuery query) =>
        MessageDtoMapper.ToDto(message, new MessageMappingOptions(query.Select));

    private static async Task<IResult> StreamAsync(Result<BlobContent> content, string? contentType, CancellationToken cancellationToken)
    {
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var blob = content.Value;
        var stream = await blob.Open(cancellationToken);
        return Results.Stream(stream, contentType ?? blob.ContentType);
    }

    internal static MeetingMessageDetails? ToDetails(MeetingDetails? meeting) => meeting is null
        ? null
        : new MeetingMessageDetails
        {
            Start = meeting.Start,
            End = meeting.End,
            IsAllDay = meeting.IsAllDay,
            Location = meeting.Location,
            MessageType = meeting.MessageType,
            ProposedStart = meeting.ProposedStart,
            ProposedEnd = meeting.ProposedEnd,
        };
}

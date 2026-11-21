using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Features.Mail.Write.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>Message and draft writes: PATCH, create draft, delete, move, permanent delete, migration import, draft attachments.</summary>
internal static class MessageWriteEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        // mail.messages.update-read-state and mail.drafts.update share this route; the body decides.
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPatch("/messages/{messageId}", UpdateAsync);
            scope.MapPost("/messages", CreateDraftAsync);
            scope.MapDelete("/messages/{messageId}", DeleteAsync);
            scope.MapPatch("/mailFolders/{folderId}/messages/{messageId}", UpdateAsync);
            scope.MapDelete("/mailFolders/{folderId}/messages/{messageId}", DeleteAsync);
            scope.MapPost("/messages/{messageId}/move", MoveAsync);
            scope.MapPost("/messages/{messageId}/copy", CopyAsync);
            scope.MapPost("/messages/{messageId}/permanentDelete", PermanentDeleteAsync);
            scope.MapPost("/mailFolders/{folderId}/messages", ImportAsync);
            scope.MapPost("/messages/{messageId}/attachments", AddAttachmentAsync);
            scope.MapDelete("/messages/{messageId}/attachments/{attachmentId}", RemoveAttachmentAsync);
        }
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string messageId, ODataQuery odata, [FromBody] JsonObject? body, UpdateMessage useCase, CancellationToken cancellationToken)
    {
        var update = MailWriteRequestMapper.ToUpdate(body);
        if (!update.IsSuccess)
        {
            return GraphResults.Error(update.Error);
        }

        var select = odata.Query.Select;
        var message = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), update.Value, new MessageFetch(select), cancellationToken);
        return GraphResults.Match(message, m =>
            GraphResults.Entity(MessageDtoMapper.ToDto(m, new MessageMappingOptions(Select: select)), Messages(mailbox), select));
    }

    private static async Task<IResult> CreateDraftAsync(Mailbox mailbox, [FromBody] MessageDto? body, CreateDraft useCase, CancellationToken cancellationToken)
    {
        var content = MailWriteRequestMapper.ToContent(body);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var draft = await useCase.ExecuteAsync(mailbox.Ref, content.Value, cancellationToken);
        return GraphResults.Match(draft, m => GraphResults.Created(MessageDtoMapper.ToDto(m), Messages(mailbox)));
    }

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string messageId, DeleteMessage useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken));

    private static async Task<IResult> CopyAsync(
        Mailbox mailbox, string messageId, [FromBody] MoveRequestDto? body, CopyMessage useCase, CancellationToken cancellationToken)
    {
        var destination = WriteFolders.Parse(body?.DestinationId, "destinationId");
        if (!destination.IsSuccess)
        {
            return GraphResults.Error(destination.Error);
        }

        var copied = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), destination.Value, cancellationToken);
        return GraphResults.Match(copied, m => GraphResults.CreatedAt(
            MessageDtoMapper.ToDto(m), Messages(mailbox), $"{mailbox.ContextSegment}/messages('{Uri.EscapeDataString(m.Id.Value)}')"));
    }

    private static async Task<IResult> MoveAsync(
        Mailbox mailbox, string messageId, [FromBody] MoveRequestDto? body, MoveMessage useCase, CancellationToken cancellationToken)
    {
        var destination = WriteFolders.Parse(body?.DestinationId, "destinationId");
        if (!destination.IsSuccess)
        {
            return GraphResults.Error(destination.Error);
        }

        var moved = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), destination.Value, cancellationToken);
        return GraphResults.Match(moved, m => GraphResults.Created(MessageDtoMapper.ToDto(m), Messages(mailbox)));
    }

    private static async Task<IResult> PermanentDeleteAsync(Mailbox mailbox, string messageId, PermanentlyDeleteMessage useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken));

    private static async Task<IResult> ImportAsync(
        Mailbox mailbox, string folderId, [FromBody] MessageDto? body, ImportMessage useCase, CancellationToken cancellationToken)
    {
        var folder = WriteFolders.Parse(folderId, "folderId");
        var content = MailWriteRequestMapper.ToContent(body);
        if (!folder.IsSuccess || !content.IsSuccess)
        {
            return GraphResults.Error(folder.Error ?? content.Error!);
        }

        var options = MapiProperties.ToImportOptions(body!);
        if (!options.IsSuccess)
        {
            return GraphResults.Error(options.Error);
        }

        var imported = await useCase.ExecuteAsync(mailbox.Ref, folder.Value, content.Value, options.Value, cancellationToken);
        return GraphResults.Match(imported, m =>
            GraphResults.Created(MessageDtoMapper.ToDto(m), $"{mailbox.ContextSegment}/mailFolders('{folderId}')/messages"));
    }

    private static async Task<IResult> AddAttachmentAsync(
        Mailbox mailbox, string messageId, [FromBody] AttachmentDto? body, AddDraftAttachment useCase, CancellationToken cancellationToken)
    {
        var attachment = MailWriteRequestMapper.ToAttachment(body);
        if (!attachment.IsSuccess)
        {
            return GraphResults.Error(attachment.Error);
        }

        var added = await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), attachment.Value, cancellationToken);
        return GraphResults.Match(added, a =>
            GraphResults.Created(MessageDtoMapper.ToDto(a), $"{mailbox.ContextSegment}/messages('{messageId}')/attachments"));
    }

    private static async Task<IResult> RemoveAttachmentAsync(
        Mailbox mailbox, string messageId, string attachmentId, RemoveDraftAttachment useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new MessageId(messageId), new AttachmentId(attachmentId), cancellationToken));

    private static string Messages(Mailbox mailbox) => $"{mailbox.ContextSegment}/messages";
}

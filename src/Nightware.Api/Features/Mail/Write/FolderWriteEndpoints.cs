using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Features.Mail.Write.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>
/// Folder writes. <c>POST mailFolders</c> (a root create) is not in the contract: NightMail creates top-level
/// folders as <c>childFolders</c> of <c>msgfolderroot</c>.
/// </summary>
internal static class FolderWriteEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/mailFolders/{folderId}/childFolders", CreateChildAsync);
            scope.MapPatch("/mailFolders/{folderId}", RenameAsync);
            scope.MapDelete("/mailFolders/{folderId}", DeleteAsync);
            scope.MapPost("/mailFolders/{folderId}/move", MoveAsync);
        }
    }

    private static async Task<IResult> CreateChildAsync(
        Mailbox mailbox, string folderId, [FromBody] MailFolderRequestDto? body, CreateChildFolder useCase, CancellationToken cancellationToken)
    {
        var parent = WriteFolders.Parse(folderId, "folderId");
        if (!parent.IsSuccess)
        {
            return GraphResults.Error(parent.Error);
        }

        var created = await useCase.ExecuteAsync(mailbox.Ref, parent.Value, body?.DisplayName, cancellationToken);
        return GraphResults.Match(created, f =>
            GraphResults.Created(MessageDtoMapper.ToDto(f), $"{mailbox.ContextSegment}/mailFolders('{folderId}')/childFolders"));
    }

    private static async Task<IResult> RenameAsync(
        Mailbox mailbox, string folderId, ODataQuery odata, [FromBody] MailFolderRequestDto? body, RenameFolder useCase, CancellationToken cancellationToken)
    {
        var renamed = await useCase.ExecuteAsync(mailbox.Ref, new FolderId(folderId), body?.DisplayName, cancellationToken);
        return GraphResults.Match(renamed, f => GraphResults.Entity(MessageDtoMapper.ToDto(f), Folders(mailbox), odata.Query.Select));
    }

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string folderId, DeleteFolder useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new FolderId(folderId), cancellationToken));

    private static async Task<IResult> MoveAsync(
        Mailbox mailbox, string folderId, [FromBody] MoveRequestDto? body, MoveFolder useCase, CancellationToken cancellationToken)
    {
        var destination = WriteFolders.Parse(body?.DestinationId, "destinationId");
        if (!destination.IsSuccess)
        {
            return GraphResults.Error(destination.Error);
        }

        var moved = await useCase.ExecuteAsync(mailbox.Ref, new FolderId(folderId), destination.Value, cancellationToken);
        return GraphResults.Match(moved, f => GraphResults.Created(MessageDtoMapper.ToDto(f), Folders(mailbox)));
    }

    private static string Folders(Mailbox mailbox) => $"{mailbox.ContextSegment}/mailFolders";
}

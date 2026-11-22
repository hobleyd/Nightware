using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Contacts.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Contacts;

public static class ContactsEndpoints
{
    /// <summary>
    /// Maps the Contacts routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>): contacts CRUD
    /// and their photo. <c>GET {mailbox}/contacts</c> spans every address book (NightMail's typeahead cache
    /// relies on it), where Graph lists only the default folder; new contacts go to the default folder.
    /// </summary>
    public static RouteGroupBuilder MapContactsEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/contacts", ListAsync).WithCount();
            scope.MapGet("/contacts/$count", ListAsync).AsCountSegment();
            scope.MapPost("/contacts", CreateAsync);
            scope.MapGet("/contacts/{contactId}", GetAsync);
            scope.MapPatch("/contacts/{contactId}", UpdateAsync);
            scope.MapDelete("/contacts/{contactId}", DeleteAsync);
            // Graph's folder-scoped item routes. Contact ids are unique in the mailbox, so the folder is not checked.
            scope.MapGet("/contactFolders/{folderId}/contacts/{contactId}", GetAsync);
            scope.MapPatch("/contactFolders/{folderId}/contacts/{contactId}", UpdateAsync);
            scope.MapDelete("/contactFolders/{folderId}/contacts/{contactId}", DeleteAsync);
            scope.MapGet("/contacts/{contactId}/photo/$value", GetPhotoAsync);
            scope.MapPut("/contacts/{contactId}/photo/$value", SetPhotoAsync);

            scope.MapGet("/contactFolders/delta", FoldersDeltaAsync);

            // Graph's own deltaLinks use the key form, contactFolders('{id}'), so a client following one lands here.
            scope.MapGet("/contactFolders/{folderId}/contacts/delta", ContactsDeltaAsync);
            scope.MapGet("/contactFolders('{folderId}')/contacts/delta", ContactsDeltaAsync);
            scope.MapGet("/contactFolders", ListFoldersAsync);
            scope.MapPost("/contactFolders", CreateFolderAsync);
            scope.MapGet("/contactFolders/{folderId}", GetFolderAsync);
            scope.MapPatch("/contactFolders/{folderId}", RenameFolderAsync);
            scope.MapDelete("/contactFolders/{folderId}", DeleteFolderAsync);
            scope.MapGet("/contactFolders/{folderId}/childFolders", ChildFoldersAsync);
            scope.MapGet("/contactFolders/{folderId}/contacts", FolderContactsAsync).WithCount();
            scope.MapGet("/contactFolders/{folderId}/contacts/$count", FolderContactsAsync).AsCountSegment();
            scope.MapPost("/contactFolders/{folderId}/contacts", CreateInFolderAsync);
        }

        return graph;
    }

    private static string Contacts(Mailbox mailbox) => $"{mailbox.ContextSegment}/contacts";

    private static string Folders(Mailbox mailbox) => $"{mailbox.ContextSegment}/contactFolders";

    private static string Folder(Mailbox mailbox, string folderId) => $"{Folders(mailbox)}('{Uri.EscapeDataString(folderId)}')";

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListContacts useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ContactDtoMapper.ToDto, Contacts(mailbox), odata.Query));

    private static async Task<IResult> GetAsync(Mailbox mailbox, string contactId, ODataQuery odata, GetContact useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new ContactId(contactId), cancellationToken).ConfigureAwait(false),
            c => GraphResults.Entity(ContactDtoMapper.ToDto(c), Contacts(mailbox), odata.Query.Select));

    private static async Task<IResult> CreateAsync(Mailbox mailbox, JsonObject? body, CreateContact useCase, CancellationToken cancellationToken)
    {
        var draft = ContactDtoMapper.ReadDraft(body);
        if (!draft.IsSuccess)
        {
            return GraphResults.Error(draft.Error);
        }

        var created = await useCase.ExecuteAsync(mailbox.Ref, null, draft.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(created, c => GraphResults.CreatedAt(ContactDtoMapper.ToDto(c), Contacts(mailbox), $"{Contacts(mailbox)}('{Uri.EscapeDataString(c.Id.Value)}')"));
    }

    private static async Task<IResult> UpdateAsync(Mailbox mailbox, string contactId, JsonObject? body, UpdateContact useCase, CancellationToken cancellationToken)
    {
        var patch = ContactDtoMapper.ReadDraft(body);
        if (!patch.IsSuccess)
        {
            return GraphResults.Error(patch.Error);
        }

        var updated = await useCase.ExecuteAsync(mailbox.Ref, new ContactId(contactId), patch.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(updated, c => GraphResults.Entity(ContactDtoMapper.ToDto(c), Contacts(mailbox)));
    }

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string contactId, DeleteContact useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new ContactId(contactId), cancellationToken).ConfigureAwait(false), GraphResults.NoContent);

    private static async Task<IResult> ListFoldersAsync(Mailbox mailbox, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ListAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false),
            folders => GraphResults.Collection(folders.Select(ContactFolderDto.From), Folders(mailbox), ItemQuery.Default));

    private static async Task<IResult> GetFolderAsync(Mailbox mailbox, string folderId, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.GetAsync(mailbox.Ref, folderId, cancellationToken).ConfigureAwait(false),
            f => GraphResults.Entity(ContactFolderDto.From(f), Folders(mailbox)));

    private static async Task<IResult> CreateFolderAsync(Mailbox mailbox, ContactFolderWriteDto? body, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.CreateAsync(mailbox.Ref, body?.DisplayName, cancellationToken).ConfigureAwait(false),
            f => GraphResults.CreatedAt(ContactFolderDto.From(f), Folders(mailbox), Folder(mailbox, f.Id.Value)));

    private static async Task<IResult> RenameFolderAsync(Mailbox mailbox, string folderId, ContactFolderWriteDto? body, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.RenameAsync(mailbox.Ref, folderId, body?.DisplayName, cancellationToken).ConfigureAwait(false),
            f => GraphResults.Entity(ContactFolderDto.From(f), Folders(mailbox)));

    private static async Task<IResult> DeleteFolderAsync(Mailbox mailbox, string folderId, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.DeleteAsync(mailbox.Ref, folderId, cancellationToken).ConfigureAwait(false), GraphResults.NoContent);

    private static async Task<IResult> ChildFoldersAsync(Mailbox mailbox, string folderId, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ChildFoldersAsync(mailbox.Ref, folderId, cancellationToken).ConfigureAwait(false),
            folders => GraphResults.Collection(folders.Select(ContactFolderDto.From), $"{Folder(mailbox, folderId)}/childFolders", ItemQuery.Default));

    private static async Task<IResult> FolderContactsAsync(Mailbox mailbox, string folderId, ODataQuery odata, ContactFoldersUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ContactsAsync(mailbox.Ref, folderId, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ContactDtoMapper.ToDto, $"{Folder(mailbox, folderId)}/contacts", odata.Query));

    private static async Task<IResult> CreateInFolderAsync(Mailbox mailbox, string folderId, JsonObject? body, ContactFoldersUseCase useCase, CancellationToken cancellationToken)
    {
        var draft = ContactDtoMapper.ReadDraft(body);
        if (!draft.IsSuccess)
        {
            return GraphResults.Error(draft.Error);
        }

        var created = await useCase.CreateContactAsync(mailbox.Ref, folderId, draft.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(created, c => GraphResults.CreatedAt(
            ContactDtoMapper.ToDto(c), $"{Folder(mailbox, folderId)}/contacts", $"{Folder(mailbox, folderId)}/contacts('{Uri.EscapeDataString(c.Id.Value)}')"));
    }

    private static async Task<IResult> FoldersDeltaAsync(
        Mailbox mailbox, ODataQuery odata, HttpContext http, Graph.Json.DeltaLinkCodec links, SyncContactFolders useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(http.Request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var page = await useCase.ExecuteAsync(mailbox.Ref, cursor.Value.Token, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p => GraphResults.Delta(p, ContactFolderDto.From, cursor.Value, "contactFolder", "#microsoft.graph.contactFolder", typeUpserts: true));
    }

    private static async Task<IResult> ContactsDeltaAsync(
        Mailbox mailbox, string folderId, ODataQuery odata, HttpContext http, Graph.Json.DeltaLinkCodec links, SyncFolderContacts useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(http.Request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var page = await useCase.ExecuteAsync(mailbox.Ref, Uri.UnescapeDataString(folderId), cursor.Value.Token, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p => GraphResults.Delta(p, ContactDtoMapper.ToDto, cursor.Value, "contact", "#microsoft.graph.contact", typeUpserts: true));
    }

    private static async Task<IResult> GetPhotoAsync(Mailbox mailbox, string contactId, GetContactPhoto useCase, CancellationToken cancellationToken)
    {
        var photo = await useCase.ExecuteAsync(mailbox.Ref, new ContactId(contactId), cancellationToken).ConfigureAwait(false);
        if (!photo.IsSuccess)
        {
            return GraphResults.Error(photo.Error);
        }

        var stream = await photo.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, photo.Value.ContentType);
    }

    /// <summary>Graph answers a photo upload with 200 and no body (as captured).</summary>
    private static async Task<IResult> SetPhotoAsync(Mailbox mailbox, string contactId, HttpRequest request, SetContactPhoto useCase, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > SetContactPhoto.MaxBytes)
            {
                return GraphResults.Error(Error.Validation($"The photo is larger than {SetContactPhoto.MaxBytes / (1024 * 1024)} MB."));
            }
        }

        var result = await useCase.ExecuteAsync(mailbox.Ref, new ContactId(contactId), buffer.ToArray(), request.ContentType, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, static () => Results.StatusCode(StatusCodes.Status200OK));
    }
}

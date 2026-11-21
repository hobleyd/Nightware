using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Contacts.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
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
            scope.MapGet("/contacts", ListAsync);
            scope.MapPost("/contacts", CreateAsync);
            scope.MapGet("/contacts/{contactId}", GetAsync);
            scope.MapPatch("/contacts/{contactId}", UpdateAsync);
            scope.MapDelete("/contacts/{contactId}", DeleteAsync);
            scope.MapGet("/contacts/{contactId}/photo/$value", GetPhotoAsync);
            scope.MapPut("/contacts/{contactId}/photo/$value", SetPhotoAsync);
        }

        return graph;
    }

    private static string Contacts(Mailbox mailbox) => $"{mailbox.ContextSegment}/contacts";

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

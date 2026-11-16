using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Contacts.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Contacts;

namespace Nightware.Api.Features.Contacts;

public static class ContactsEndpoints
{
    /// <summary>
    /// Maps the Contacts routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>):
    /// <c>GET {mailbox}/contacts</c>. Contacts are read-only per the contract, so no write routes exist.
    /// </summary>
    public static RouteGroupBuilder MapContactsEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/contacts", ListAsync);
        }
        return graph;
    }

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListContacts useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, ContactDtoMapper.ToDto, $"{mailbox.ContextSegment}/contacts", odata.Query));
}

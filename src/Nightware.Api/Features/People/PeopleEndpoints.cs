using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.People.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.People;

namespace Nightware.Api.Features.People;

public static class PeopleEndpoints
{
    /// <summary>
    /// Maps the People routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>):
    /// <c>GET {mailbox}/people</c> (no <c>$search</c> per the contract).
    /// </summary>
    public static RouteGroupBuilder MapPeopleEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/people", ListAsync);
        }
        return graph;
    }

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListPeople useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, PersonDtoMapper.ToDto, $"{mailbox.ContextSegment}/people", odata.Query));
}

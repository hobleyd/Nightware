using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Rooms.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Errors;
using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;

namespace Nightware.Api.Features.Rooms;

public static class RoomsEndpoints
{
    public const string PlacesContext = "places/microsoft.graph.room";
    public const string FindRoomsContext = "Collection(microsoft.graph.emailAddress)";

    /// <summary>
    /// Maps the Rooms routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>); see docs/api-plumbing.md.
    /// Neither route is mailbox-scoped: the room list is the tenant's, and NightMail always asks
    /// <c>beta/me/findRooms</c>, even for a shared mailbox.
    /// </summary>
    public static RouteGroupBuilder MapRoomsEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapGet("/places/microsoft.graph.room", ListPlacesAsync);
        graph.MapGet("/me/findRooms", FindRoomsAsync);
        graph.MapGet("/places/microsoft.graph.roomList", ListRoomListsAsync);
        graph.MapGet("/places/{roomList}/microsoft.graph.roomList/rooms", ListRoomsInListAsync);
        return graph;
    }

    /// <summary>Phase 19: the buildings as room lists (see <see cref="RoomLists"/>).</summary>
    private static async Task<IResult> ListRoomListsAsync(ODataQuery odata, ListRoomLists useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            lists => GraphResults.Collection(lists.Select(RoomDtoMapper.ToDto), "places/microsoft.graph.roomList", odata.Query));

    private static async Task<IResult> ListRoomsInListAsync(string roomList, ODataQuery odata, ListRoomsInList useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(roomList, cancellationToken).ConfigureAwait(false),
            rooms => GraphResults.Collection(rooms.Select(RoomDtoMapper.ToDto), $"places('{Uri.EscapeDataString(roomList)}')/microsoft.graph.roomList/rooms", odata.Query));

    private static async Task<IResult> ListPlacesAsync(ODataQuery odata, ListRooms rooms, CancellationToken cancellationToken)
    {
        var page = await rooms.ExecuteAsync(odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p => GraphResults.Page(p, RoomDtoMapper.ToDto, PlacesContext, odata.Query));
    }

    private static async Task<IResult> FindRoomsAsync(HttpContext http, FindRooms findRooms, CancellationToken cancellationToken)
    {
        // findRooms exists only in Graph beta; under v1.0 answer as Graph does for any unknown segment.
        if (GraphPaths.VersionOf(http.Request.Path) != GraphPaths.Beta)
        {
            return new GraphErrorResult(new GraphErrorDescriptor(
                StatusCodes.Status400BadRequest, UnknownGraphRoute.Code, "Resource not found for the segment 'findRooms'.", null));
        }

        var result = await findRooms.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, rooms => GraphResults.Collection(rooms.Select(RoomDtoMapper.ToDto), FindRoomsContext, ItemQuery.Default));
    }
}

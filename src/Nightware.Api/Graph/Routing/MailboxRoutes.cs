using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Graph.Routing;

/// <summary>
/// The two Graph forms of a mailbox-scoped route, <c>/me{pattern}</c> and <c>/users/{user}{pattern}</c>,
/// as route groups. Map each route once per group; bind a <see cref="Mailbox"/> parameter in the
/// handler to get the resolved <see cref="Domain.Kernel.MailboxRef"/>.
/// </summary>
/// <remarks>
/// Routes are mapped with the framework's own <c>MapGet</c>/<c>MapPost</c>/… at the call site, not
/// through a helper taking a <see cref="Delegate"/>: the Request Delegate Generator can only generate
/// a handler it sees there, and a handler it cannot see is compiled from expression trees on every
/// host start.
/// </remarks>
/// <example>
/// <code>
/// foreach (var scope in graph.MapMailboxGroups())
/// {
///     scope.MapGet("/mailFolders/{folderId}", async (Mailbox mailbox, string folderId, ODataQuery odata, ...) => ...);
/// }
/// </code>
/// </example>
public static class MailboxRoutes
{
    public const string MePrefix = "/me";
    public const string UsersPrefix = "/users/{" + Mailbox.RouteParameter + "}";

    /// <summary>The <c>/me</c> group and the <c>/users/{user}</c> group under <paramref name="graph"/>.</summary>
    public static IReadOnlyList<RouteGroupBuilder> MapMailboxGroups(this IEndpointRouteBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return [graph.MapGroup(MePrefix), graph.MapGroup(UsersPrefix)];
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Directory.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Directory;

public static class DirectoryEndpoints
{
    private const string UsersContext = "users";

    /// <summary>
    /// Maps the Directory routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>):
    /// <c>GET /me</c>, <c>GET /users/{id|upn}</c>, <c>GET /users</c> and <c>GET /users/{id|upn}/photo/$value</c>.
    /// </summary>
    /// <remarks>
    /// <c>/users/{x}</c> is plain routing rather than a mailbox route: it is both the contact-card lookup and
    /// the shared mailbox's profile (<c>directory.me.get</c> through the mailbox base), and every miss there
    /// must be a 404 the client swallows — not the mailbox resolver's answers.
    /// </remarks>
    public static RouteGroupBuilder MapDirectoryEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.MapGet("/me", GetMeAsync);
        graph.MapGet("/users", ListUsersAsync);
        graph.MapGet("/users/{user}", GetUserAsync);
        graph.MapGet("/users/{user}/photo/$value", GetPhotoAsync);
        return graph;
    }

    private static async Task<IResult> GetMeAsync(ODataQuery odata, GetSignedInUser useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false),
            user => GraphResults.Entity(UserDtoMapper.ToDto(user, odata.Query.Select), UsersContext, odata.Query.Select));

    private static async Task<IResult> GetUserAsync(string user, ODataQuery odata, GetUser useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(user, cancellationToken).ConfigureAwait(false),
            found => GraphResults.Entity(UserDtoMapper.ToDto(found, odata.Query.Select), UsersContext, odata.Query.Select));

    private static async Task<IResult> ListUsersAsync(ODataQuery odata, ListUsers useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, u => UserDtoMapper.ToDto(u, odata.Query.Select), UsersContext, odata.Query));

    private static async Task<IResult> GetPhotoAsync(string user, GetUserPhoto useCase, CancellationToken cancellationToken)
    {
        var photo = await useCase.ExecuteAsync(user, cancellationToken).ConfigureAwait(false);
        if (!photo.IsSuccess)
        {
            return GraphResults.Error(photo.Error);
        }

        BlobContent blob = photo.Value;
        var stream = await blob.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, blob.ContentType);
    }
}

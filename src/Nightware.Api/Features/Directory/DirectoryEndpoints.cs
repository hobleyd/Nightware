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
    /// <c>GET /me</c>, <c>GET /users/{id|upn}</c>, <c>GET /users</c>, <c>GET /users/{id|upn}/photo/$value</c>, and the phase 19
    /// photo routes (<c>/me/photo/$value</c>, the <c>…/photo</c> metadata, and PUT/PATCH/DELETE of one's own photo).
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

        // Phase 19: the signed-in user's photo, the metadata, and setting or removing one's own.
        graph.MapGet("/me/photo/$value", GetMyPhotoAsync);
        graph.MapGet("/me/photo", (GetUserPhotoInfo useCase, CancellationToken ct) => PhotoInfoAsync(null, useCase, ct));
        graph.MapGet("/users/{user}/photo", (string user, GetUserPhotoInfo useCase, CancellationToken ct) => PhotoInfoAsync(user, useCase, ct));
        graph.MapPut("/me/photo/$value", (HttpRequest request, SetUserPhoto useCase, CancellationToken ct) => SetPhotoAsync(null, request, useCase, ct));
        graph.MapPatch("/me/photo/$value", (HttpRequest request, SetUserPhoto useCase, CancellationToken ct) => SetPhotoAsync(null, request, useCase, ct));
        graph.MapPut("/users/{user}/photo/$value", (string user, HttpRequest request, SetUserPhoto useCase, CancellationToken ct) => SetPhotoAsync(user, request, useCase, ct));
        graph.MapPatch("/users/{user}/photo/$value", (string user, HttpRequest request, SetUserPhoto useCase, CancellationToken ct) => SetPhotoAsync(user, request, useCase, ct));
        graph.MapDelete("/me/photo/$value", (DeleteUserPhoto useCase, CancellationToken ct) => DeletePhotoAsync(null, useCase, ct));
        graph.MapDelete("/users/{user}/photo/$value", (string user, DeleteUserPhoto useCase, CancellationToken ct) => DeletePhotoAsync(user, useCase, ct));
        return graph;
    }

    private static async Task<IResult> GetMyPhotoAsync(GetSignedInUserPhoto useCase, CancellationToken cancellationToken)
    {
        var photo = await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!photo.IsSuccess)
        {
            return GraphResults.Error(photo.Error);
        }

        var stream = await photo.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, photo.Value.ContentType);
    }

    private static async Task<IResult> PhotoInfoAsync(string? user, GetUserPhotoInfo useCase, CancellationToken cancellationToken)
    {
        var context = user is null ? "me/photo" : $"users('{Uri.EscapeDataString(user)}')/photo";
        return GraphResults.Match(
            await useCase.ExecuteAsync(user, cancellationToken).ConfigureAwait(false),
            info => GraphResults.Entity(ProfilePhotoDto.From(info), context));
    }

    /// <summary>Graph answers a photo upload with 200 and no body (profilephoto-update).</summary>
    private static async Task<IResult> SetPhotoAsync(string? user, HttpRequest request, SetUserPhoto useCase, CancellationToken cancellationToken)
    {
        // Read one byte past the limit, so the use case sees an oversize upload without buffering all of it.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > UserPhotos.MaxUploadBytes)
            {
                break;
            }
        }

        var result = await useCase.ExecuteAsync(user, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, static () => Results.StatusCode(StatusCodes.Status200OK));
    }

    /// <summary>Graph answers a photo removal with 200 (profilephoto-delete).</summary>
    private static async Task<IResult> DeletePhotoAsync(string? user, DeleteUserPhoto useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(user, cancellationToken).ConfigureAwait(false),
            static () => Results.StatusCode(StatusCodes.Status200OK));

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

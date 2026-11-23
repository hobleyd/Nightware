using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Presence.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Presence;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Presence;

/// <summary>
/// Presence over Nextcloud user status (phase 25, ADR 0009 D6): read anyone's under <c>/me</c>, <c>/users/{id}</c> and
/// <c>/communications</c>; set only the caller's.
/// </summary>
public static class PresenceEndpoints
{
    public static RouteGroupBuilder MapPresenceEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/presence", GetAsync);
            scope.MapPost("/presence/setPresence", SetPresenceAsync);
            scope.MapPost("/presence/clearPresence", ClearPresenceAsync);
            scope.MapPost("/presence/setUserPreferredPresence", SetPreferredAsync);
            scope.MapPost("/presence/clearUserPreferredPresence", ClearPreferredAsync);
            scope.MapPost("/presence/setStatusMessage", SetStatusMessageAsync);
        }

        graph.MapGet("/communications/presences/{userId}", GetByIdAsync);
        graph.MapPost("/communications/getPresencesByUserId", GetByIdsAsync);
        return graph;
    }

    private static async Task<IResult> GetAsync(Mailbox mailbox, ODataQuery odata, GetPresence useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.IsMe ? null : mailbox.Ref.Address, cancellationToken).ConfigureAwait(false),
            presence => GraphResults.Entity(PresenceDto.From(presence), $"{mailbox.ContextSegment}/presence", odata.Query.Select));

    private static async Task<IResult> GetByIdAsync(string userId, ODataQuery odata, GetPresence useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(userId, cancellationToken).ConfigureAwait(false),
            presence => GraphResults.Entity(PresenceDto.From(presence), "communications/presences", odata.Query.Select));

    private static async Task<IResult> GetByIdsAsync(JsonObject? body, ODataQuery odata, GetPresencesByUserId useCase, CancellationToken cancellationToken)
    {
        var ids = PresenceRequests.ReadIds(body);
        if (!ids.IsSuccess)
        {
            return GraphResults.Error(ids.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(ids.Value, cancellationToken).ConfigureAwait(false),
            presences => GraphResults.Collection(presences.Select(static p => (object)PresenceDto.From(p)), "Collection(microsoft.graph.presence)", odata.Query));
    }

    private static async Task<IResult> SetPresenceAsync(Mailbox mailbox, JsonObject? body, SetPresence useCase, CancellationToken cancellationToken)
    {
        var change = PresenceRequests.ReadChange(body);
        return Ok(change.IsSuccess ? await useCase.ExecuteAsync(Target(mailbox), change.Value, cancellationToken).ConfigureAwait(false) : change.Error);
    }

    private static async Task<IResult> SetPreferredAsync(Mailbox mailbox, JsonObject? body, SetUserPreferredPresence useCase, CancellationToken cancellationToken)
    {
        var change = PresenceRequests.ReadChange(body);
        return Ok(change.IsSuccess ? await useCase.ExecuteAsync(Target(mailbox), change.Value, cancellationToken).ConfigureAwait(false) : change.Error);
    }

    private static async Task<IResult> ClearPresenceAsync(Mailbox mailbox, JsonObject? body, ClearPresence useCase, CancellationToken cancellationToken)
    {
        var sessionId = PresenceRequests.ReadSessionId(body);
        return Ok(sessionId.IsSuccess ? await useCase.ExecuteAsync(Target(mailbox), sessionId.Value, cancellationToken).ConfigureAwait(false) : sessionId.Error);
    }

    private static async Task<IResult> ClearPreferredAsync(Mailbox mailbox, ClearUserPreferredPresence useCase, CancellationToken cancellationToken) =>
        Ok(await useCase.ExecuteAsync(Target(mailbox), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SetStatusMessageAsync(Mailbox mailbox, JsonObject? body, SetStatusMessage useCase, CancellationToken cancellationToken)
    {
        var change = PresenceRequests.ReadStatusMessage(body);
        return Ok(change.IsSuccess ? await useCase.ExecuteAsync(Target(mailbox), change.Value, cancellationToken).ConfigureAwait(false) : change.Error);
    }

    private static MailboxRef? Target(Mailbox mailbox) => mailbox.IsMe ? null : mailbox.Ref;

    /// <summary>Graph answers the presence actions <c>200 OK</c> with no body.</summary>
    private static IResult Ok(Result result) => GraphResults.Match(result, static () => Results.StatusCode(StatusCodes.Status200OK));
}

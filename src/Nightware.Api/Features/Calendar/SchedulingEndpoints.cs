using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Calendar.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.Scheduling;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using EventId = Nightware.Domain.Kernel.EventId;

namespace Nightware.Api.Features.Calendar;

/// <summary>The meeting actions on an event: RSVP, cancel and forward. Each answers 202 like Graph.</summary>
internal static class SchedulingEndpoints
{
    public static void Map(RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/events/{eventId}/accept", (Mailbox mailbox, string eventId, RsvpBodyDto? body, RespondToEvent respond, CancellationToken ct) =>
                RespondAsync(mailbox, eventId, RsvpResponse.Accept, body, respond, ct));
            scope.MapPost("/events/{eventId}/tentativelyAccept", (Mailbox mailbox, string eventId, RsvpBodyDto? body, RespondToEvent respond, CancellationToken ct) =>
                RespondAsync(mailbox, eventId, RsvpResponse.TentativelyAccept, body, respond, ct));
            scope.MapPost("/events/{eventId}/decline", (Mailbox mailbox, string eventId, RsvpBodyDto? body, RespondToEvent respond, CancellationToken ct) =>
                RespondAsync(mailbox, eventId, RsvpResponse.Decline, body, respond, ct));
            scope.MapPost("/events/{eventId}/cancel", CancelAsync);
            scope.MapPost("/events/{eventId}/forward", ForwardAsync);
        }
    }

    /// <summary>Maps an RSVP body; <c>sendResponse</c> defaults to true, as in Graph.</summary>
    internal static Result<RsvpRequest> ToRequest(RsvpResponse response, RsvpBodyDto? body)
    {
        var proposal = EventRequestMapper.ToTimeSlot(body?.ProposedNewTime);
        return proposal.IsSuccess
            ? new RsvpRequest(response, body?.Comment, body?.SendResponse ?? true, proposal.Value)
            : proposal.Error;
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> RespondAsync(
        Mailbox mailbox, string eventId, RsvpResponse response, RsvpBodyDto? body, RespondToEvent respond, CancellationToken cancellationToken)
    {
        var request = ToRequest(response, body);
        if (!request.IsSuccess)
        {
            return GraphResults.Error(request.Error);
        }

        var result = await respond.ExecuteAsync(mailbox.Ref, new EventId(eventId), request.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, GraphResults.Accepted);
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> CancelAsync(
        Mailbox mailbox, string eventId, CancelBodyDto? body, CancelEvent cancel, CancellationToken cancellationToken)
    {
        var result = await cancel.ExecuteAsync(mailbox.Ref, new EventId(eventId), body?.Comment, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, GraphResults.Accepted);
    }

    private static async Task<Microsoft.AspNetCore.Http.IResult> ForwardAsync(
        Mailbox mailbox, string eventId, ForwardBodyDto? body, ForwardEvent forward, CancellationToken cancellationToken)
    {
        var recipients = EventRequestMapper.ToAddresses(body?.ToRecipients, "toRecipients");
        if (!recipients.IsSuccess)
        {
            return GraphResults.Error(recipients.Error);
        }

        var result = await forward.ExecuteAsync(mailbox.Ref, new EventId(eventId), recipients.Value, body?.Comment, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, GraphResults.Accepted);
    }
}

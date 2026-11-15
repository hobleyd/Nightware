using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Calendar.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.Messages;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calendar;

/// <summary>
/// The meeting an invitation or response mail is about, and RSVPs sent from the mail. Only these literal
/// spellings are mapped (ADR 0001 §13), never a catch-all under <c>messages/{id}/</c>: NightMail tries
/// the casts in turn and moves on when one answers 400 or 404.
/// </summary>
internal static class MessageEventEndpoints
{
    /// <summary>The path segments between <c>messages/{id}</c> and <c>event</c> that NightMail uses (empty = uncast).</summary>
    public static readonly IReadOnlyList<string> Casts =
    [
        "microsoft.graph.eventMessage",
        "microsoft.graph.eventMessageRequest",
        "microsoft.graph.eventMessageResponse",
        string.Empty,
    ];

    public static void Map(RouteGroupBuilder graph)
    {
        foreach (var cast in Casts)
        {
            var segment = cast.Length == 0 ? string.Empty : "/" + cast;
            graph.MapMailboxGet($"/messages/{{messageId}}{segment}/event", (Mailbox mailbox, string messageId, ODataQuery odata, PreferHeader prefer, HttpContext http, GetMessageEvent resolve, CancellationToken ct) =>
                LinkedEventAsync(mailbox, messageId, segment, odata, prefer, http, resolve, ct));
        }

        graph.MapMailboxPost("/messages/{messageId}/accept", (Mailbox mailbox, string messageId, RsvpBodyDto? body, RespondToMessage respond, CancellationToken ct) =>
            RespondAsync(mailbox, messageId, RsvpResponse.Accept, body, respond, ct));
        graph.MapMailboxPost("/messages/{messageId}/tentativelyAccept", (Mailbox mailbox, string messageId, RsvpBodyDto? body, RespondToMessage respond, CancellationToken ct) =>
            RespondAsync(mailbox, messageId, RsvpResponse.TentativelyAccept, body, respond, ct));
        graph.MapMailboxPost("/messages/{messageId}/decline", (Mailbox mailbox, string messageId, RsvpBodyDto? body, RespondToMessage respond, CancellationToken ct) =>
            RespondAsync(mailbox, messageId, RsvpResponse.Decline, body, respond, ct));
    }

    private static async Task<IResult> LinkedEventAsync(
        Mailbox mailbox, string messageId, string castSegment, ODataQuery odata, PreferHeader prefer, HttpContext http, GetMessageEvent resolve, CancellationToken cancellationToken)
    {
        var result = await resolve.ExecuteAsync(mailbox.Ref, new MessageId(messageId), cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, e =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            var context = $"{mailbox.ContextSegment}/messages('{Uri.EscapeDataString(messageId)}'){castSegment}/event";
            return GraphResults.Entity(EventDtoMapper.ToDto(e, prefer), context, odata.Query.Select);
        });
    }

    private static async Task<IResult> RespondAsync(
        Mailbox mailbox, string messageId, RsvpResponse response, RsvpBodyDto? body, RespondToMessage respond, CancellationToken cancellationToken)
    {
        var request = SchedulingEndpoints.ToRequest(response, body);
        if (!request.IsSuccess)
        {
            return GraphResults.Error(request.Error);
        }

        var result = await respond.ExecuteAsync(mailbox.Ref, new MessageId(messageId), request.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, GraphResults.Accepted);
    }
}

using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Features.Search;
using Nightware.Api.Graph.Json;
using Nightware.Application.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Chats;

/// <summary>
/// Microsoft Search (<see cref="SearchQueryEndpoint"/>) for <c>chatMessage</c> (ADR 0009 D10): the caller's chat messages
/// by text, newest first. A hit's resource is Microsoft Search's trimmed <c>chatMessage</c> (no body: the client reads
/// the message through <c>/chats/{chatId}/messages/{id}</c>), and its summary the text around the match.
/// </summary>
internal static class ChatSearchQuery
{
    public const string ChatMessageType = "chatMessage";

    public static async Task<Result<SearchHitsContainerDto>> SearchAsync(SearchRequest request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var useCase = services.GetRequiredService<SearchChatMessages>();
        var hits = await useCase.ExecuteAsync(request.Text, request.From, request.Size ?? SearchChatMessages.DefaultSize, cancellationToken).ConfigureAwait(false);
        if (!hits.IsSuccess)
        {
            return hits.Error;
        }

        return new SearchHitsContainerDto
        {
            Hits = hits.Value.Hits.Select((hit, i) => new SearchHitDto
            {
                HitId = hit.Id,
                Rank = request.From + i + 1,
                Summary = hit.Summary,
                Resource = Resource(hit),
            }).ToList(),
            Total = request.From + hits.Value.Hits.Count,
            MoreResultsAvailable = hits.Value.MoreAvailable,
        };
    }

    /// <summary>The hit's <c>chatMessage</c> as Microsoft Search writes one (its etag is the id; a chat's <c>channelIdentity</c> is empty).</summary>
    internal static JsonObject Resource(ChatMessageHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var created = GraphDateTimeFormat.Instant(hit.CreatedDateTime);
        var resource = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.chatMessage",
            ["id"] = hit.Id,
            ["createdDateTime"] = created,
            ["lastModifiedDateTime"] = created,
            ["importance"] = "normal",
        };
        if (hit.WebUrl is { } url)
        {
            resource["webLink"] = url;
        }

        if (hit.From is { } from)
        {
            var address = new JsonObject { ["name"] = from.Name };
            if (from.Address is { } mail)
            {
                address["address"] = mail;
            }

            resource["from"] = new JsonObject { ["emailAddress"] = address };
        }

        resource["channelIdentity"] = new JsonObject();
        resource["etag"] = hit.Id;
        resource["chatId"] = hit.ChatId;
        return resource;
    }
}

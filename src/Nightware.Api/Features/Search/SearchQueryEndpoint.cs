using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Chats;
using Nightware.Api.Features.Files.Drives;
using Nightware.Api.Graph;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Search;

/// <summary>
/// Microsoft Search (<c>POST /search/query</c>): one request of one entity type, answered by the feature that owns it:
/// <c>driveItem</c> by the drive (<see cref="DriveSearchQueryEndpoint"/>, ADR 0008), <c>chatMessage</c> by chats
/// (<see cref="ChatSearchQuery"/>, ADR 0009 D10). Other types (mail, events, sites), and types mixed in one request,
/// are <c>400 notSupported</c>, which NightMail's cloud picker answers by searching the drive itself.
/// </summary>
public static class SearchQueryEndpoint
{
    public const string NotSupportedCode = "notSupported";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.MapPost("/search/query", QueryAsync);
        return graph;
    }

    private static async Task<IResult> QueryAsync(JsonObject? body, HttpContext http, CancellationToken cancellationToken)
    {
        var request = Parse(body);
        if (!request.IsSuccess)
        {
            return GraphResults.Error(request.Error);
        }

        // Each type's use case is resolved only when asked for: a host without chats still searches its drive.
        var container = request.Value.EntityType switch
        {
            DriveSearchQueryEndpoint.DriveItemType => await DriveSearchQueryEndpoint.SearchAsync(request.Value, http.RequestServices, cancellationToken).ConfigureAwait(false),
            _ => await ChatSearchQuery.SearchAsync(request.Value, http.RequestServices, cancellationToken).ConfigureAwait(false),
        };
        if (!container.IsSuccess)
        {
            return request.Value.EntityType == DriveSearchQueryEndpoint.DriveItemType ? DriveRequests.Fail(container.Error) : GraphResults.Error(container.Error);
        }

        var response = new SearchResponseDto
        {
            SearchTerms = request.Value.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            HitsContainers = [container.Value],
        };
        return GraphResults.Property(new { value = new[] { response } }, "Collection(microsoft.graph.searchResponse)");
    }

    /// <summary>The <c>requests</c> array as the one search it may hold, of a type searched here.</summary>
    internal static Result<SearchRequest> Parse(JsonObject? body)
    {
        if (body?["requests"] is not JsonArray { Count: 1 } requests || requests[0] is not JsonObject request)
        {
            return Error.Validation("requests must hold one search request.", "requests");
        }

        var types = (request["entityTypes"] as JsonArray ?? [])
            .Select(static t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty)
            .ToList();
        if (types.Count == 0)
        {
            return Error.Validation("entityTypes is required.", "entityTypes");
        }

        var type = types.Count == 1 ? Supported(types[0]) : null;
        if (type is null)
        {
            return NotSupported(
                "Only driveItem or chatMessage is searched here, one type per request; search mail with $search on messages.", "entityTypes");
        }

        var text = request["query"]?["queryString"] is JsonValue q && q.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("query.queryString is required.", "query.queryString");
        }

        if (!TryInt(request["from"], out var from) || !TryInt(request["size"], out var size))
        {
            return Error.Validation("from and size must be whole numbers.", "from");
        }

        return new SearchRequest(type, text, from ?? 0, size);
    }

    public static Error NotSupported(string message, string? target = null) => new(ErrorKind.Validation, NotSupportedCode, message, target);

    private static string? Supported(string type) =>
        string.Equals(type, DriveSearchQueryEndpoint.DriveItemType, StringComparison.OrdinalIgnoreCase) ? DriveSearchQueryEndpoint.DriveItemType
        : string.Equals(type, ChatSearchQuery.ChatMessageType, StringComparison.OrdinalIgnoreCase) ? ChatSearchQuery.ChatMessageType
        : null;

    private static bool TryInt(JsonNode? node, out int? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue v && v.TryGetValue(out int number))
        {
            value = number;
            return true;
        }

        return false;
    }

    private sealed record SearchResponseDto
    {
        public required IReadOnlyList<string> SearchTerms { get; init; }

        public required IReadOnlyList<SearchHitsContainerDto> HitsContainers { get; init; }
    }
}

/// <summary>One search request: its one entity type, the query text, and the window (<c>size</c> null for the type's default).</summary>
internal sealed record SearchRequest(string EntityType, string Text, int From, int? Size);

/// <summary>Graph <c>searchHitsContainer</c>.</summary>
internal sealed record SearchHitsContainerDto
{
    public required IReadOnlyList<SearchHitDto> Hits { get; init; }

    public int Total { get; init; }

    public bool MoreResultsAvailable { get; init; }
}

/// <summary>Graph <c>searchHit</c>; the resource carries its own <c>@odata.type</c>.</summary>
internal sealed record SearchHitDto
{
    public required string HitId { get; init; }

    public int Rank { get; init; }

    public string Summary { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Resource { get; init; }
}

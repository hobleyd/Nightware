using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;
using Nightware.Api.Features.Files.Drives.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// Microsoft Search (<c>POST /search/query</c>) for <c>driveItem</c> only: one request, searched by name over the
/// caller's drive as <c>root/search</c> is. Other entity types (mail, events, sites) are <c>400 notSupported</c>, which
/// NightMail's cloud picker answers by searching the drive itself.
/// </summary>
internal static class DriveSearchQueryEndpoint
{
    public const string DriveItemType = "driveItem";

    public static void Map(RouteGroupBuilder graph) => graph.MapPost("/search/query", QueryAsync);

    private static async Task<IResult> QueryAsync(JsonObject? body, IOptions<DiscoveryOptions> discovery, QueryDriveItems useCase, CancellationToken cancellationToken)
    {
        var request = Parse(body);
        if (!request.IsSuccess)
        {
            return DriveRequests.Fail(request.Error);
        }

        var (text, from, size) = request.Value;
        var hits = await useCase.ExecuteAsync(text, from, size, cancellationToken).ConfigureAwait(false);
        if (!hits.IsSuccess)
        {
            return DriveRequests.Fail(hits.Error);
        }

        var filesBase = discovery.Value.FilesBaseUrls?.FirstOrDefault();
        var container = new SearchHitsContainerDto
        {
            Hits = hits.Value.Items.Select((item, i) => new SearchHitDto
            {
                HitId = item.Id,
                Rank = from + i + 1,
                Resource = Resource(DriveItemResourceDto.From(item, filesBase)),
            }).ToList(),
            Total = from + hits.Value.Items.Count,
            MoreResultsAvailable = hits.Value.MoreAvailable,
        };
        var response = new SearchResponseDto
        {
            SearchTerms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            HitsContainers = [container],
        };
        return GraphResults.Property(new { value = new[] { response } }, "Collection(microsoft.graph.searchResponse)");
    }

    /// <summary>The <c>requests</c> array as the one drive search it may hold: (queryString, from, size).</summary>
    internal static Result<(string Text, int From, int Size)> Parse(JsonObject? body)
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

        if (types.Any(static t => !string.Equals(t, DriveItemType, StringComparison.OrdinalIgnoreCase)))
        {
            return DriveErrors.NotSupported("Only driveItem is searched here; search mail with $search on messages.", "entityTypes");
        }

        var text = request["query"]?["queryString"] is JsonValue q && q.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Error.Validation("query.queryString is required.", "query.queryString");
        }

        if (!TryInt(request["from"], 0, out var from) || !TryInt(request["size"], QueryDriveItems.DefaultSize, out var size))
        {
            return Error.Validation("from and size must be whole numbers.", "from");
        }

        return (text, from, size);
    }

    private static bool TryInt(JsonNode? node, int fallback, out int value)
    {
        value = fallback;
        return node is null || (node is JsonValue v && v.TryGetValue(out value));
    }

    /// <summary>A hit's resource: the driveItem with its type, as Microsoft Search writes it.</summary>
    private static JsonObject Resource(DriveItemResourceDto item)
    {
        var resource = new JsonObject { ["@odata.type"] = "#microsoft.graph.driveItem" };
        foreach (var (key, value) in GraphJson.ToObject(item).ToList())
        {
            resource[key] = value?.DeepClone();
        }

        return resource;
    }

    private sealed record SearchResponseDto
    {
        public required IReadOnlyList<string> SearchTerms { get; init; }

        public required IReadOnlyList<SearchHitsContainerDto> HitsContainers { get; init; }
    }

    private sealed record SearchHitsContainerDto
    {
        public required IReadOnlyList<SearchHitDto> Hits { get; init; }

        public int Total { get; init; }

        public bool MoreResultsAvailable { get; init; }
    }

    private sealed record SearchHitDto
    {
        public required string HitId { get; init; }

        public int Rank { get; init; }

        public string Summary { get; init; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonObject? Resource { get; init; }
    }
}

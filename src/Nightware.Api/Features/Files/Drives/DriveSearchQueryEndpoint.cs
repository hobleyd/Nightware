using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;
using Nightware.Api.Features.Files.Drives.Dto;
using Nightware.Api.Features.Search;
using Nightware.Api.Graph.Json;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files.Drives;

/// <summary>
/// Microsoft Search (<see cref="SearchQueryEndpoint"/>) for <c>driveItem</c>: searched by name over the caller's drive as
/// <c>root/search</c> is; each hit's resource is the driveItem with its type.
/// </summary>
internal static class DriveSearchQueryEndpoint
{
    public const string DriveItemType = "driveItem";

    public static async Task<Result<SearchHitsContainerDto>> SearchAsync(SearchRequest request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var useCase = services.GetRequiredService<QueryDriveItems>();
        var hits = await useCase.ExecuteAsync(request.Text, request.From, request.Size ?? QueryDriveItems.DefaultSize, cancellationToken).ConfigureAwait(false);
        if (!hits.IsSuccess)
        {
            return hits.Error;
        }

        var filesBase = services.GetRequiredService<IOptions<DiscoveryOptions>>().Value.FilesBaseUrls?.FirstOrDefault();
        return new SearchHitsContainerDto
        {
            Hits = hits.Value.Items.Select((item, i) => new SearchHitDto
            {
                HitId = item.Id,
                Rank = request.From + i + 1,
                Resource = Resource(DriveItemResourceDto.From(item, filesBase)),
            }).ToList(),
            Total = request.From + hits.Value.Items.Count,
            MoreResultsAvailable = hits.Value.MoreAvailable,
        };
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
}

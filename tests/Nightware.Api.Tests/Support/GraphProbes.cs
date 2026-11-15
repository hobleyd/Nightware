using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Composition;

namespace Nightware.Api.Tests.Support;

/// <summary>
/// Test-only routes mapped inside the real <c>/v1.0</c> and <c>/beta</c> groups (so they get auth,
/// CORS, the binding filter and the fallback ordering exactly like a feature route).
/// </summary>
internal sealed class GraphProbes(Action<RouteGroupBuilder> map) : IGraphEndpointContributor
{
    public void Map(RouteGroupBuilder graph) => map(graph);
}

internal static class GraphProbeServiceCollectionExtensions
{
    public static IServiceCollection AddGraphProbes(this IServiceCollection services, Action<RouteGroupBuilder> map) =>
        services.AddSingleton<IGraphEndpointContributor>(new GraphProbes(map));
}

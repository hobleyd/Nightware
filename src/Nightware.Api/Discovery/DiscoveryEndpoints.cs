using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Json;

namespace Nightware.Api.Discovery;

public static class DiscoveryEndpoints
{
    public const string Path = "/.well-known/nightware";

    /// <summary>Maps the anonymous discovery document.</summary>
    public static IEndpointConventionBuilder MapDiscovery(this IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(Path, static (IOptions<DiscoveryOptions> options) =>
                Results.Json(options.Value, GraphJson.Options))
            .AllowAnonymous()
            .ExcludeFromDescription();
}

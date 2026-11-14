using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Routing;

/// <summary>
/// An endpoint parameter that binds itself from the request (via <c>BindAsync</c>) and may fail
/// with a Graph error instead of the framework's bare 400.
/// </summary>
public interface IGraphBoundParameter
{
    /// <summary>Why binding failed, or null when the value is usable.</summary>
    Error? BindingError { get; }
}

/// <summary>
/// Short-circuits an endpoint with the Graph error envelope when any <see cref="IGraphBoundParameter"/>
/// argument failed to bind. Added to the <c>/v1.0</c> and <c>/beta</c> groups, so every Graph
/// endpoint gets it.
/// </summary>
public sealed class GraphBindingFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        foreach (var argument in context.Arguments)
        {
            if (argument is IGraphBoundParameter { BindingError: { } error })
            {
                return GraphResults.Error(error);
            }
        }

        return await next(context).ConfigureAwait(false);
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Graph.Batch;

/// <summary>
/// The request pipeline <c>$batch</c> sub-requests run through: the same routing, CORS, limits,
/// authentication, rate limiting, budget and authorization as a direct call, wrapped in the Graph
/// exception envelope. Built once by <c>UseNightware</c>, after every endpoint is mapped.
/// </summary>
public sealed class GraphBatchPipeline
{
    private RequestDelegate? _pipeline;

    public RequestDelegate Pipeline =>
        _pipeline ?? throw new InvalidOperationException("The batch pipeline is built by UseNightware.");

    /// <summary>
    /// Builds the pipeline as a branch of <paramref name="app"/>. <paramref name="configure"/> adds the
    /// middleware between the exception envelope and the endpoints.
    /// </summary>
    public void Build(IApplicationBuilder app, Action<IApplicationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configure);
        var branch = app.New();

        // WebApplication.New() drops the global endpoint route builder so that a branch routes over its
        // own endpoints; this branch must route over the app's, so its properties are put back.
        foreach (var (key, value) in app.Properties)
        {
            branch.Properties[key] = value;
        }

        branch.UseMiddleware<Errors.GraphExceptionMiddleware>();
        configure(branch);
        branch.UseEndpoints(static _ => { });
        _pipeline = branch.Build();
    }
}

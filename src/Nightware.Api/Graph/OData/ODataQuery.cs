using System.Reflection;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.OData;

/// <summary>
/// The parsed OData query options of a request, as an endpoint parameter. A parse failure
/// becomes a <c>400 invalidRequest</c> through <see cref="GraphBindingFilter"/> before the handler runs.
/// </summary>
public sealed class ODataQuery : IGraphBoundParameter
{
    private readonly ItemQuery? _query;

    private ODataQuery(ItemQuery? query, Error? error)
    {
        _query = query;
        BindingError = error;
    }

    public Error? BindingError { get; }

    /// <summary>The parsed query. Only read it from a handler (the filter guarantees success there).</summary>
    public ItemQuery Query => _query ?? throw new InvalidOperationException($"OData binding failed: {BindingError?.Message}");

    public static ODataQuery From(Result<ItemQuery> result) =>
        result.IsSuccess ? new ODataQuery(result.Value, null) : new ODataQuery(null, result.Error);

    public static ValueTask<ODataQuery?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult<ODataQuery?>(From(ODataQueryParser.Parse(context.Request.Query)));
    }
}

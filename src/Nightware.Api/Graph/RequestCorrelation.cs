using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nightware.Api.Graph;

/// <summary>Per-request correlation ids, emitted as headers and inside every error's <c>innerError</c>.</summary>
public sealed record RequestCorrelation(string RequestId, string? ClientRequestId)
{
    public const string RequestIdHeader = "request-id";
    public const string ClientRequestIdHeader = "client-request-id";

    /// <summary>Longest client-request-id echoed back; anything longer is ignored (Graph caps it too).</summary>
    public const int MaxClientRequestIdLength = 200;

    /// <summary>The correlation for <paramref name="context"/>, creating it if the middleware has not run (e.g. unit tests).</summary>
    public static RequestCorrelation Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<RequestCorrelation>() is { } existing)
        {
            return existing;
        }

        var created = FromRequest(context.Request);
        context.Features.Set(created);
        return created;
    }

    internal static RequestCorrelation FromRequest(HttpRequest request)
    {
        var client = request.Headers[ClientRequestIdHeader].ToString();
        var valid = !string.IsNullOrWhiteSpace(client)
            && client.Length <= MaxClientRequestIdLength
            && client.All(c => c is >= ' ' and <= '~');
        return new RequestCorrelation(Guid.NewGuid().ToString(), valid ? client : null);
    }
}

/// <summary>
/// Assigns <c>request-id</c>, echoes <c>client-request-id</c>, and puts both on the request's trace
/// span and log scope so a client-reported id finds the server's logs and traces.
/// </summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    /// <summary>Log-scope and span attribute names.</summary>
    public const string RequestIdTag = "graph.request_id";
    public const string ClientRequestIdTag = "graph.client_request_id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = RequestCorrelation.Get(context);
        context.TraceIdentifier = correlation.RequestId;
        context.Response.OnStarting(static state =>
        {
            var (ctx, ids) = ((HttpContext, RequestCorrelation))state;
            ctx.Response.Headers[RequestCorrelation.RequestIdHeader] = ids.RequestId;
            if (ids.ClientRequestId is not null)
            {
                ctx.Response.Headers[RequestCorrelation.ClientRequestIdHeader] = ids.ClientRequestId;
            }

            return Task.CompletedTask;
        }, (context, correlation));

        var activity = Activity.Current;
        activity?.SetTag(RequestIdTag, correlation.RequestId);
        if (correlation.ClientRequestId is not null)
        {
            activity?.SetTag(ClientRequestIdTag, correlation.ClientRequestId);
        }

        using (logger.BeginScope(new CorrelationScope(correlation)))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>A structured scope (key/value pairs) that also formats readably in console output.</summary>
    private sealed class CorrelationScope(RequestCorrelation correlation) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 2;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new(RequestIdTag, correlation.RequestId),
            1 => new(ClientRequestIdTag, correlation.ClientRequestId),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return this[0];
            yield return this[1];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() =>
            $"request-id:{correlation.RequestId} client-request-id:{correlation.ClientRequestId ?? "-"}";
    }
}

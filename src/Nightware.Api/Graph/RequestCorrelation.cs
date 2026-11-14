using Microsoft.AspNetCore.Http;

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

/// <summary>Assigns <c>request-id</c>, echoes <c>client-request-id</c>.</summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = RequestCorrelation.Get(context);
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
        return next(context);
    }
}

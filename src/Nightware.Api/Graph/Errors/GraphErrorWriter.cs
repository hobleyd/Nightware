using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Graph.Json;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Errors;

/// <summary>
/// Writes Graph's error envelope:
/// <c>{"error":{"code","message","innerError":{"date","request-id","client-request-id"}}}</c>.
/// </summary>
public static class GraphErrorWriter
{
    /// <summary>Graph's <c>innerError.date</c> format: UTC, whole seconds, no zone designator.</summary>
    public const string InnerErrorDateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public static Task WriteAsync(HttpContext context, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return WriteAsync(context, GraphErrorMapper.Map(error));
    }

    public static async Task WriteAsync(HttpContext context, GraphErrorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        var response = context.Response;
        response.StatusCode = descriptor.StatusCode;
        if (descriptor.RetryAfterSeconds is { } retryAfter)
        {
            response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        response.ContentType = "application/json";
        var body = Envelope(context, descriptor);
        await JsonSerializer.SerializeAsync(response.Body, body, GraphJson.Options, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The envelope as a node, for tests and for callers composing their own response.</summary>
    public static JsonObject Envelope(HttpContext context, GraphErrorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);
        var correlation = RequestCorrelation.Get(context);
        var clock = context.RequestServices?.GetService<TimeProvider>() ?? TimeProvider.System;

        var inner = new JsonObject
        {
            ["date"] = clock.GetUtcNow().UtcDateTime.ToString(InnerErrorDateFormat, CultureInfo.InvariantCulture),
            ["request-id"] = correlation.RequestId,
            ["client-request-id"] = correlation.ClientRequestId ?? correlation.RequestId,
        };

        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = descriptor.Code,
                ["message"] = descriptor.Message,
                ["innerError"] = inner,
            },
        };
    }
}

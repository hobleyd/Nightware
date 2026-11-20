using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Batch;

/// <summary>A sub-request's answer: its status and the JSON object written into <c>responses</c>.</summary>
public sealed record BatchResponse(int Status, JsonObject Json);

/// <summary>
/// Runs one batch entry through <see cref="GraphBatchPipeline"/> as an in-process request of its own:
/// a fresh <see cref="HttpContext"/> and DI scope, the caller's <c>Authorization</c> and the batch's
/// scheme, host and path base (so links come out as they would on a direct call).
/// </summary>
public sealed class BatchSubRequest(GraphBatchPipeline pipeline, IServiceScopeFactory scopes)
{
    // Headers that describe the transport, not the sub-request's response.
    private static readonly HashSet<string> TransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.ContentLength, HeaderNames.TransferEncoding, HeaderNames.Connection,
    };

    public async Task<BatchResponse> RunAsync(HttpContext batch, BatchItem item)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(item);

        var target = Target(batch, item);
        if (!target.IsSuccess)
        {
            return Refuse(batch, item, target.Error);
        }

        var body = BodyBytes(item);
        if (!body.IsSuccess)
        {
            return Refuse(batch, item, body.Error);
        }

        IHeaderDictionary headers = new HeaderDictionary();
        foreach (var (name, value) in item.Headers)
        {
            headers[name] = value;
        }

        headers.Host = batch.Request.Host.ToString();
        headers.Authorization = batch.Request.Headers.Authorization;
        if (StringValues.IsNullOrEmpty(headers.Accept))
        {
            headers.Accept = "application/json";
        }

        headers.ContentLength = body.Value.Length;

        await using var scope = scopes.CreateAsyncScope();
        var response = new BatchResponseFeature();
        var responseBody = new MemoryStream();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Protocol = batch.Request.Protocol,
            Scheme = batch.Request.Scheme,
            Method = item.Method,
            PathBase = batch.Request.PathBase,
            Path = target.Value.Path,
            QueryString = target.Value.Query.ToUriComponent(),
            Headers = headers,
            Body = new MemoryStream(body.Value),
        });
        // Minimal APIs bind a JSON body only when the server says the request can have one.
        features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(body.Value.Length > 0));
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = batch.RequestAborted });
        features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature());
        features.Set<IServiceProvidersFeature>(new ServiceProvidersFeature { RequestServices = scope.ServiceProvider });
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            ConnectionId = batch.Connection.Id,
            RemoteIpAddress = batch.Connection.RemoteIpAddress,
            RemotePort = batch.Connection.RemotePort,
            LocalIpAddress = batch.Connection.LocalIpAddress,
            LocalPort = batch.Connection.LocalPort,
        });
        var context = new DefaultHttpContext(features);

        try
        {
            await pipeline.Pipeline(context).ConfigureAwait(false);
            await response.StartAsync().ConfigureAwait(false);
            return Read(item, context.Response, responseBody.ToArray());
        }
        finally
        {
            await response.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static Result<(PathString Path, QueryString Query)> Target(HttpContext batch, BatchItem item)
    {
        var url = item.Url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return Error.Validation("A batch request's url must be relative to the version root, e.g. '/me/messages'.");
        }

        var queryAt = url.IndexOf('?', StringComparison.Ordinal);
        var path = queryAt < 0 ? url : url[..queryAt];
        var query = queryAt < 0 ? QueryString.Empty : new QueryString(url[queryAt..]);
        var full = new PathString(GraphPaths.VersionOf(batch.Request.Path)).Add(new PathString("/" + path.TrimStart('/')));

        if (full.Value!.Split('/').Any(static s => s.Equals("$batch", StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Validation("A batch request cannot itself be a $batch.");
        }

        // A self-authorising URL carries its own credential (ADR 0001 §39); the batch carries the caller's.
        if (QueryHelpers.ParseQuery(query.Value).ContainsKey("authtoken"))
        {
            return Error.Validation("A self-authorising URL cannot be called inside a batch.");
        }

        return (full, query);
    }

    private static Result<byte[]> BodyBytes(BatchItem item)
    {
        if (item.Body is null)
        {
            return Array.Empty<byte>();
        }

        if (!item.Headers.TryGetValue(HeaderNames.ContentType, out var contentType))
        {
            return Error.Validation($"Request '{item.Id}' has a body but no Content-Type header.");
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.SerializeToUtf8Bytes(item.Body);
        }

        if (item.Body is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return Error.Validation($"Request '{item.Id}': a {contentType} body must be a string.");
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(text);
        }

        // Graph carries any other body base64-encoded.
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return Error.Validation($"Request '{item.Id}': a {contentType} body must be base64-encoded.");
        }
    }

    private static BatchResponse Refuse(HttpContext batch, BatchItem item, Error error)
    {
        var descriptor = GraphErrorMapper.Map(error);
        return new BatchResponse(descriptor.StatusCode, new JsonObject
        {
            ["id"] = item.Id,
            ["status"] = descriptor.StatusCode,
            ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
            ["body"] = GraphErrorWriter.Envelope(batch, descriptor),
        });
    }

    private static BatchResponse Read(BatchItem item, HttpResponse response, byte[] body)
    {
        var headers = new JsonObject();
        foreach (var (name, value) in response.Headers)
        {
            if (!TransportHeaders.Contains(name))
            {
                headers[name] = value.ToString();
            }
        }

        var json = new JsonObject
        {
            ["id"] = item.Id,
            ["status"] = response.StatusCode,
            ["headers"] = headers,
        };
        if (body.Length > 0)
        {
            json["body"] = BodyNode(response.ContentType, body);
        }

        return new BatchResponse(response.StatusCode, json);
    }

    private static JsonNode? BodyNode(string? contentType, byte[] body)
    {
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                // Not JSON after all: fall through to the base64 form rather than lose the body.
            }
        }

        return contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            ? Encoding.UTF8.GetString(body)
            : Convert.ToBase64String(body);
    }

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;

    /// <summary>
    /// A response feature that runs its OnStarting/OnCompleted callbacks when asked, since nothing
    /// sends this response: middleware that sets headers on start, or disposes on completion, still does.
    /// </summary>
    private sealed class BatchResponseFeature : IHttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();
        private readonly Stack<(Func<object, Task> Callback, object State)> _completed = new();

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        [Obsolete("Use IHttpResponseBodyFeature.Stream.")]
        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _starting.Push((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state) => _completed.Push((callback, state));

        public async Task StartAsync()
        {
            while (_starting.TryPop(out var entry))
            {
                await entry.Callback(entry.State).ConfigureAwait(false);
            }

            HasStarted = true;
        }

        public async Task CompleteAsync()
        {
            while (_completed.TryPop(out var entry))
            {
                await entry.Callback(entry.State).ConfigureAwait(false);
            }
        }
    }
}

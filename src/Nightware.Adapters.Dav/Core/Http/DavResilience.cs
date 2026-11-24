using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Nightware.Adapters.Dav.Core.Http;

/// <summary>
/// Retry policy for the DAV client: only methods that are safe to repeat are retried.
/// <c>DisableForUnsafeHttpMethods()</c> is not enough — it knows nothing of MKCOL,
/// MKCALENDAR, MOVE or PROPPATCH, which must never be replayed.
/// </summary>
public static class DavResilience
{
    public const string PipelineName = "nightware-dav";

    private static readonly HashSet<string> RetryableMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "PROPFIND", "REPORT",
    };

    public static bool IsRetryable(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return RetryableMethods.Contains(method.Method);
    }

    /// <summary>Transient failure (5xx, 408, network error, attempt timeout) of a safe method; never 429.</summary>
    public static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args)
    {
        var request = args.Context.GetRequestMessage() ?? args.Outcome.Result?.RequestMessage;
        if (request is null || !IsRetryable(request.Method))
        {
            return ValueTask.FromResult(false);
        }

        // 429 is surfaced (Throttled + Retry-After), never replayed: honouring a long Retry-After here
        // would stall the caller until the total timeout and lose the throttling signal.
        if (args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome));
    }

    /// <summary>
    /// What the circuit breaker counts: the server down or failing as a whole (500, 502, 503, 504, a network error,
    /// an attempt timeout). Never a 4xx, 429 included, nor a per-user 5xx such as 507 (quota), so one caller's
    /// requests can't open the circuit for everyone.
    /// </summary>
    public static bool IsServerFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result?.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// The breaker: open for 15 s once half of at least 20 attempts in 30 s failed (<see cref="IsServerFailure"/>),
    /// so callers get a 503 at once instead of each waiting out the timeouts of a server that is down.
    /// </summary>
    public static HttpCircuitBreakerStrategyOptions CircuitBreaker() => new()
    {
        FailureRatio = 0.5,
        MinimumThroughput = 20,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(15),
        ShouldHandle = static args => ValueTask.FromResult(IsServerFailure(args.Outcome)),
    };

    /// <summary>True for a request whose body streams from the caller, which takes <see cref="DavOptions.TransferTimeout"/>.</summary>
    public static bool IsStreamedUpload(HttpRequestMessage? request) => request?.Content is StreamContent;

    /// <summary>
    /// Total timeout → retry (safe methods) → circuit breaker → per-attempt timeout. A streamed upload has
    /// <see cref="DavOptions.TransferTimeout"/> for both timeouts.
    /// </summary>
    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, DavOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.AddTimeout(Timeout(options.TotalTimeout, options.TransferTimeout));
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldHandle = ShouldRetry,
                ShouldRetryAfterHeader = false, // short backoff only; Retry-After is reported to the caller
            });
        }

        builder.AddCircuitBreaker(CircuitBreaker());
        builder.AddTimeout(Timeout(options.AttemptTimeout, options.TransferTimeout));
    }

    private static TimeoutStrategyOptions Timeout(TimeSpan ordinary, TimeSpan transfer) => new()
    {
        Timeout = ordinary,
        TimeoutGenerator = args => ValueTask.FromResult(IsStreamedUpload(args.Context.GetRequestMessage()) ? transfer : ordinary),
    };
}

using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

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

    /// <summary>Total timeout → retry (safe methods) → per-attempt timeout.</summary>
    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, DavOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        builder.AddTimeout(options.TotalTimeout);
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

        builder.AddTimeout(options.AttemptTimeout);
    }
}

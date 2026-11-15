using System.Globalization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Throttling;

/// <summary>
/// The per-user limiter on <c>/v1.0</c> and <c>/beta</c>: a token bucket (rate) chained with a
/// concurrency limit, both partitioned by the caller's <c>sub</c>. A rejection is Graph's
/// <c>429 TooManyRequests</c> with an integer <c>Retry-After</c>, which NightMail's
/// <c>RetryInterceptor</c> honours.
/// </summary>
/// <remarks>
/// <para>Nothing is queued: a queued request spends the client's 30 s receive timeout waiting, and
/// an immediate 429 with <c>Retry-After</c> gives the client the same pause without holding a
/// connection. Discovery, <c>/healthz</c> and CORS preflights are never limited. A request with no
/// valid token is partitioned by client address (after forwarded-header processing), so garbage
/// tokens cannot exhaust a real user's bucket.</para>
/// <para>The limiter must run after authentication (it needs <c>sub</c>) and before the request
/// timeout, so a rejected request costs nothing downstream.</para>
/// </remarks>
public static class GraphRateLimiting
{
    /// <summary>Retry-After for a concurrency rejection, which carries no metadata of its own.</summary>
    public const int ConcurrencyRetryAfterSeconds = 1;

    public static IServiceCollection AddGraphRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>());

        services.AddRateLimiter(static _ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitingOptions>>(static (limiter, configured) =>
            {
                var options = configured.Value;
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = static (context, _) => new ValueTask(WriteRejectionAsync(context));
                if (options.Enabled)
                {
                    limiter.GlobalLimiter = CreateLimiter(options);
                }
            });
        return services;
    }

    /// <summary>The partitioned limiter; public for tests that drive it without a host.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rate = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            PartitionKey(context) is { } key
                ? RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.TokenLimit,
                    TokensPerPeriod = options.TokensPerPeriod,
                    ReplenishmentPeriod = options.ReplenishmentPeriod,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })
                : RateLimitPartition.GetNoLimiter(string.Empty));

        if (options.ConcurrentRequestLimit == 0)
        {
            return rate;
        }

        var concurrency = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            PartitionKey(context) is { } key
                ? RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = options.ConcurrentRequestLimit,
                    QueueLimit = 0,
                })
                : RateLimitPartition.GetNoLimiter(string.Empty));

        return PartitionedRateLimiter.CreateChained(concurrency, rate);
    }

    /// <summary>
    /// <c>user:&lt;sub&gt;</c> for an authenticated Graph request, <c>ip:&lt;address&gt;</c> for an
    /// unauthenticated one, and null (unlimited) for everything that is not a Graph call.
    /// </summary>
    public static string? PartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!GraphPaths.IsGraphRequest(context.Request.Path) || HttpMethods.IsOptions(context.Request.Method))
        {
            return null;
        }

        var sub = context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(HttpUserContext.SubjectClaim)?.Value
            : null;
        return string.IsNullOrEmpty(sub)
            ? "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "user:" + sub;
    }

    /// <summary>Whole seconds until a permit is likely, never less than one.</summary>
    public static int RetryAfterSeconds(RateLimitLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : ConcurrencyRetryAfterSeconds;
    }

    private static Task WriteRejectionAsync(OnRejectedContext context)
    {
        var retryAfter = RetryAfterSeconds(context.Lease);
        var error = new Error(
            ErrorKind.Throttled,
            "TooManyRequests",
            string.Create(CultureInfo.InvariantCulture, $"Too many requests. Retry after {retryAfter} seconds."))
        {
            RetryAfterSeconds = retryAfter,
        };
        return GraphErrorWriter.WriteAsync(context.HttpContext, error);
    }
}

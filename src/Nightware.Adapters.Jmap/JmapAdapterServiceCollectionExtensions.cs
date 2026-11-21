using System.Net;
using Nightware.Adapters.Jmap.Security;
using Nightware.Adapters.Jmap.Directory;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Mail.Read;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Health;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Session;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Nightware.Adapters.Jmap;

public static class JmapAdapterServiceCollectionExtensions
{
    /// <summary>Registers the Jmap adapter: core client plus every feature port it implements.</summary>
    public static IServiceCollection AddJmapAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<JmapOptions>().Bind(configuration.GetSection(JmapOptions.SectionName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JmapOptions>, JmapOptionsValidator>());

        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJmapSessionProvider, JmapSessionProvider>();
        services.TryAddScoped<JmapConnectionProvider>();
        services.TryAddScoped<IJmapClient, JmapClient>();
        services.TryAddScoped<IJmapBlobService, JmapBlobService>();
        services.TryAddTransient<JmapAuthenticationHandler>();

        AddHttpClients(services);

        services.AddHealthChecks().AddCheck<JmapSessionHealthCheck>(JmapSessionHealthCheck.Name, tags: JmapSessionHealthCheck.Tags);

        // Each feature registers its ports from its own folder, so phases never edit this file.
        return services
            .AddJmapMailRead()
            .AddJmapMailWrite()
            .AddJmapMailboxSettings()
            .AddJmapSecurity()
            .AddJmapDirectory();
    }

    private static void AddHttpClients(IServiceCollection services)
    {
        AddResilientClient(services, JmapHttpClients.Api, "jmap-api", o => (o.RequestTimeout, o.AttemptTimeout));
        AddResilientClient(services, JmapHttpClients.Blob, "jmap-blob", o => (o.BlobTimeout, o.BlobTimeout));

        services.AddHttpClient(JmapHttpClients.Health, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(NoRedirects);
    }

    private static void AddResilientClient(IServiceCollection services, string name, string pipelineName, Func<JmapOptions, (TimeSpan Total, TimeSpan Attempt)> timeouts)
    {
        var builder = services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(NoRedirects);

        // Resilience is the outer handler; the credential handler inside it runs on every attempt.
        builder.AddResilienceHandler(pipelineName, (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<JmapOptions>>().Value;
            var (total, attempt) = timeouts(options);
            ConfigurePipeline(pipeline, total, attempt, options.MaxRetryAttempts);
        });
        builder.AddHttpMessageHandler<JmapAuthenticationHandler>();
    }

    // The primary handler strips Authorization on redirects; the session fetch follows them itself.
    // Cookies are off: pooled handlers are shared across callers, so a session cookie set for one
    // user's request would otherwise be replayed on the next user's request.
    private static SocketsHttpHandler NoRedirects() => new() { AllowAutoRedirect = false, UseCookies = false };

    /// <summary>Total timeout → retry (safe requests, transient failures) → circuit breaker → per-attempt timeout.</summary>
    internal static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, TimeSpan total, TimeSpan attempt, int maxRetryAttempts)
    {
        pipeline.AddTimeout(total);
        if (maxRetryAttempts > 0)
        {
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = maxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                ShouldRetryAfterHeader = false, // short backoff only; Retry-After is reported to the caller
                ShouldHandle = ShouldRetry,
            });
        }

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 20,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
            ShouldHandle = static args => ValueTask.FromResult(IsServerFailure(args.Outcome)),
        });
        pipeline.AddTimeout(attempt);
    }

    /// <summary>
    /// A transient failure of a safe request; never 429, which is surfaced (Throttled + Retry-After): honouring a
    /// long Retry-After here would stall the caller until the request budget answers 503.
    /// </summary>
    internal static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(args.Context.GetRequestMessage().IsSafe()
            && args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests
            && HttpClientResiliencePredicates.IsTransient(args.Outcome));

    /// <summary>
    /// What the breaker counts (open 15 s once half of at least 20 attempts in 30 s failed): the server down or
    /// failing as a whole (500, 502, 503, 504, a network error, an attempt timeout), never a 4xx or 429, so one
    /// caller's requests can't open the circuit for everyone. An open circuit is a 503 at once.
    /// </summary>
    internal static bool IsServerFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException
        || outcome.Result?.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}

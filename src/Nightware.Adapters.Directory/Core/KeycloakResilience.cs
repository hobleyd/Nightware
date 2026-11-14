using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;

namespace Nightware.Adapters.Directory.Core;

/// <summary>
/// The resilience pipeline on every Keycloak client: an outer retry for <b>safe</b> requests only
/// (GET/HEAD, or explicitly marked with <see cref="KeycloakHttp.RetrySafe"/> — the client-credentials
/// grant), and an inner per-attempt timeout. Token exchange is never marked, so a failing exchange is
/// not replayed with the same input.
/// </summary>
internal static class KeycloakResilience
{
    public static IHttpClientBuilder AddKeycloakResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("keycloak", static (pipeline, context) =>
        {
            DirectoryOptions options = context.ServiceProvider.GetRequiredService<IOptions<DirectoryOptions>>().Value;
            if (options.MaxRetryAttempts > 0)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    Delay = options.RetryBaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = static args => ValueTask.FromResult(
                        args.Context.GetRequestMessage().IsRetrySafe()
                        && HttpClientResiliencePredicates.IsTransient(args.Outcome)),
                });
            }

            pipeline.AddTimeout(options.RequestTimeout);
        });
        return builder;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;

using Polly;
using Polly.Retry;

namespace Nightware.Adapters.Cloudflare.Administration;

public static class CloudflareAdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDnsHosting"/> over Cloudflare for the admin console. Configuration (<c>Cloudflare</c>)
    /// is optional and carries no credential: every call uses the entity's own token.
    /// </summary>
    public static IServiceCollection AddCloudflareAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CloudflareOptions>()
            .Bind(configuration.GetSection(CloudflareOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CloudflareOptions>, CloudflareOptionsValidator>());

        services.AddHttpClient<ICloudflareClient, CloudflareClient>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            // Pooled handlers are shared: no cookie jar, and no redirects (which would carry the token elsewhere).
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
            .AddResilienceHandler("cloudflare", static (pipeline, context) =>
            {
                CloudflareOptions options = context.ServiceProvider.GetRequiredService<IOptions<CloudflareOptions>>().Value;
                pipeline.AddTimeout(options.RequestTimeout);
                if (options.MaxRetryAttempts > 0)
                {
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = options.MaxRetryAttempts,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(200),
                        ShouldRetryAfterHeader = true,
                        ShouldHandle = ShouldRetry,
                    });
                }

                pipeline.AddTimeout(options.AttemptTimeout);
            });

        services.TryAddScoped<IDnsHosting, CloudflareDnsHosting>();
        return services;
    }

    /// <summary>Reads only: a write that timed out may have been made, and repeating a create would duplicate the record.</summary>
    internal static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(args.Context.GetRequestMessage()?.Method == HttpMethod.Get && HttpClientResiliencePredicates.IsTransient(args.Outcome));
}

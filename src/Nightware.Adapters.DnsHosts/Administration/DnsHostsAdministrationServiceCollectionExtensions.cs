using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;

using Polly;
using Polly.Retry;

namespace Nightware.Adapters.DnsHosts.Administration;

public static class DnsHostsAdministrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IDnsHosting"/> for each DNS host besides Cloudflare: DigitalOcean, Gandi, Hetzner,
    /// Amazon Route 53, Azure DNS and Google Cloud DNS. Configuration (<c>DnsHosts</c>) is optional and carries no
    /// credential: every call uses the entity's own.
    /// </summary>
    public static IServiceCollection AddDnsHostsAdministration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DnsHostsOptions>()
            .Bind(configuration.GetSection(DnsHostsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DnsHostsOptions>, DnsHostsOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AccessTokenCache>();

        services.AddHttpClient<IDnsHostClient, DnsHostClient>(static http => http.Timeout = Timeout.InfiniteTimeSpan)
            // Pooled handlers are shared: no cookie jar, and no redirects (which would carry the credential elsewhere).
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
            .AddResilienceHandler("dns-hosts", static (pipeline, context) =>
            {
                DnsHostsOptions options = context.ServiceProvider.GetRequiredService<IOptions<DnsHostsOptions>>().Value;
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

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, DigitalOceanDnsHosting>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, GandiDnsHosting>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, HetznerDnsHosting>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, Route53DnsHosting>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, AzureDnsHosting>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDnsHosting, GoogleCloudDnsHosting>());
        return services;
    }

    /// <summary>
    /// Reads only: a write that timed out may have been made, and repeating it could duplicate a record. A signed
    /// Route 53 read is safe to resend within the five minutes its signature holds.
    /// </summary>
    internal static ValueTask<bool> ShouldRetry(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(args.Context.GetRequestMessage()?.Method == HttpMethod.Get && HttpClientResiliencePredicates.IsTransient(args.Outcome));
}

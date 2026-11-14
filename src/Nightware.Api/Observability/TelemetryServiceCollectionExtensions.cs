using System.Reflection;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nightware.Api.Observability;

public static class TelemetryServiceCollectionExtensions
{
    /// <summary>Meters exported besides the instrumentation packages' own.</summary>
    public static readonly IReadOnlyList<string> ExtraMeters = [NightwareMetrics.MeterName, "Polly"];

    /// <summary>
    /// The façade's metrics, backend-client tagging and — when <c>Telemetry:Otlp:Endpoint</c> is set —
    /// OpenTelemetry traces, metrics and logs exported over OTLP.
    /// </summary>
    /// <remarks>
    /// <para>Nothing in the SDK is registered while export is off: the framework's own meters still
    /// exist (so <c>dotnet-counters</c> works) but no spans are sampled for nobody.</para>
    /// <para>What is never exported: request and response bodies and headers. The ASP.NET Core and
    /// HttpClient instrumentations record method, route template, status and a query-redacted URL;
    /// nothing here enriches spans with headers. Log export carries the same records the console
    /// does, which the redaction tests pin.</para>
    /// </remarks>
    public static IServiceCollection AddNightwareTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TelemetryOptions>, TelemetryOptionsValidator>());

        services.AddLogRedaction();
        services.AddMetrics();
        services.TryAddSingleton<NightwareMetrics>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, BackendClientMetricsFilter>());

        var settings = new TelemetryOptions();
        configuration.GetSection(TelemetryOptions.SectionName).Bind(settings);
        if (!settings.Otlp.Enabled)
        {
            return services;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                settings.ServiceName,
                serviceVersion: typeof(TelemetryServiceCollectionExtensions).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion))
            .WithTracing(static tracing => tracing
                .AddAspNetCoreInstrumentation(static o => o.Filter = static context => !IsProbe(context))
                .AddHttpClientInstrumentation())
            .WithMetrics(static metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentationMeters()
                .AddMeter([.. ExtraMeters]))
            .WithLogging(configureBuilder: null, configureOptions: static logging =>
            {
                logging.IncludeScopes = true;
                logging.IncludeFormattedMessage = true;
            })
            .UseOtlpExporter(settings.Otlp.Protocol, new Uri(settings.Otlp.Endpoint!));

        return services;
    }

    /// <summary>Health probes run every few seconds and say nothing about user traffic.</summary>
    internal static bool IsProbe(HttpContext context) =>
        context.Request.Path.StartsWithSegments(Composition.NightwareComposition.HealthPath, StringComparison.OrdinalIgnoreCase);

    // The runtime's own meter (GC, thread pool, exceptions) needs no instrumentation package in .NET 9+.
    private static MeterProviderBuilder AddRuntimeInstrumentationMeters(this MeterProviderBuilder metrics) =>
        metrics.AddMeter("System.Runtime");
}

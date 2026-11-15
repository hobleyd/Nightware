using Microsoft.Extensions.Options;

using OpenTelemetry.Exporter;

namespace Nightware.Api.Observability;

/// <summary>OpenTelemetry export (configuration section <c>Telemetry</c>). Off unless an endpoint is set.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary><c>service.name</c> on every span, metric and log record.</summary>
    public string ServiceName { get; set; } = "nightware-api";

    public OtlpSettings Otlp { get; set; } = new();

    public sealed class OtlpSettings
    {
        /// <summary>
        /// Collector endpoint, e.g. <c>http://otel-collector:4317</c> (gRPC) or
        /// <c>http://otel-collector:4318</c> (HTTP). Empty disables traces, metrics and log export.
        /// Export headers (a collector API key) come from the standard
        /// <c>OTEL_EXPORTER_OTLP_HEADERS</c> environment variable, which the exporter reads itself.
        /// </summary>
        public string? Endpoint { get; set; }

        public OtlpExportProtocol Protocol { get; set; } = OtlpExportProtocol.Grpc;

        public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint);
    }
}

internal sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            failures.Add("Telemetry:ServiceName is required.");
        }

        if (options.Otlp.Enabled
            && !(Uri.TryCreate(options.Otlp.Endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            failures.Add("Telemetry:Otlp:Endpoint must be an absolute http(s) URL.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

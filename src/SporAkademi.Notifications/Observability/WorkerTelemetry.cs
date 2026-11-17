using System.Globalization;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Infrastructure.Observability;

namespace SporAkademi.Notifications.Observability;

/// <summary>
/// P5-04 (DEC-634): OpenTelemetry traces and metrics over OTLP, with the API's settings (<c>OpenTelemetry:Otlp:Endpoint</c> or
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OpenTelemetry:Otlp:Protocol</c>, <c>OpenTelemetry:Enabled</c>,
/// <c>OpenTelemetry:Tracing:SampleRatio</c>). Without an endpoint no span is recorded and no exporter exists. A consumer's span
/// continues the trace the API's publisher started (W3C trace context in the message headers), so one e-mail is one trace
/// across both processes; the sampling decision is the parent's. The API's custom health gauges are not repeated here: the
/// worker's probes and the broker's queue depth are the signals (docs/gozlemlenebilirlik.md).
/// </summary>
internal static class WorkerTelemetry
{
    public const string ServiceName = "spor-akademi-notifications";

    public static WebApplicationBuilder AddWorkerTelemetry(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var endpointText = FirstNonEmpty(configuration["OpenTelemetry:Otlp:Endpoint"], configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var endpoint = Uri.TryCreate(endpointText, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? parsed : null;
        var enabled = endpoint is not null || (bool.TryParse(configuration["OpenTelemetry:Enabled"], out var flag) && flag);
        var protocol = FirstNonEmpty(configuration["OpenTelemetry:Otlp:Protocol"], configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]) == "http/protobuf"
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
        var ratio = double.TryParse(configuration["OpenTelemetry:Tracing:SampleRatio"], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r is >= 0 and <= 1 ? r : 1.0;
        var version = typeof(WorkerTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing
                .SetSampler(enabled ? new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)) : new AlwaysOffSampler())
                .AddSource(AppTelemetry.Name)
                .AddSource("Npgsql")
                // Before any exporter: what leaves the process has been scrubbed (the API's scrubber, shared from Infrastructure).
                .AddProcessor(new TelemetryScrubber()))
            .WithMetrics(metrics => metrics.AddMeter(AppTelemetry.Name).AddMeter("System.Runtime"));

        if (endpoint is not null)
        {
            telemetry.UseOtlpExporter(protocol, endpoint);
        }

        return builder;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

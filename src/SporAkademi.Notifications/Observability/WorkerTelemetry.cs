using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SporAkademi.Application.Abstractions.Observability;

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
                // Before any exporter: what leaves the process has been scrubbed.
                .AddProcessor(new WorkerTelemetryScrubber()))
            .WithMetrics(metrics => metrics.AddMeter(AppTelemetry.Name).AddMeter("System.Runtime"));

        if (endpoint is not null)
        {
            telemetry.UseOtlpExporter(protocol, endpoint);
        }

        return builder;
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>Removes SQL parameters, query strings, addresses and framework exception text from every finished span.</summary>
internal sealed class WorkerTelemetryScrubber : BaseProcessor<Activity>
{
    private static readonly string[] DroppedKeys =
    [
        "url.query", "http.query", "client.address", "client.port", "network.peer.address", "network.peer.port",
        "enduser.id", "enduser.role", "db.user",
    ];

    private static readonly string[] DroppedPrefixes = ["db.query.parameter.", "db.statement.parameter"];

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var (key, _) in data.TagObjects.ToList())
        {
            if (DroppedKeys.Contains(key, StringComparer.Ordinal) || DroppedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            {
                data.SetTag(key, null);
            }
        }

        // A status description from a driver or the framework may be an exception message, which may quote personal data.
        if (data.Status == ActivityStatusCode.Error && !string.IsNullOrEmpty(data.StatusDescription))
        {
            data.SetStatus(ActivityStatusCode.Error);
        }
    }
}

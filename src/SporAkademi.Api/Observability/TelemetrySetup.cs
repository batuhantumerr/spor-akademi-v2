using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SporAkademi.Application.Abstractions.Observability;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P4-07 (NFR-OBS-04, DEC-550 – DEC-553): OpenTelemetry traces and metrics, exported over OTLP. Whether anything is
/// recorded and where it goes is decided from configuration when the providers are built, not here, so hosts and test
/// factories can supply it through any configuration source (like the cache, DEC-315). Without an endpoint (and without
/// <c>OpenTelemetry:Enabled</c>) the sampler drops every span and no metric reader exists: the API behaves as before.
/// </summary>
internal static class TelemetrySetup
{
    public const string ServiceName = "spor-akademi-api";

    /// <summary>Framework meters worth exporting; each has fixed, low-cardinality attributes (route templates, status codes, results).</summary>
    public static readonly string[] FrameworkMeters =
    [
        "Microsoft.AspNetCore.Hosting", // http.server.request.duration: error rate and p95 latency per route
        "Microsoft.AspNetCore.Diagnostics", // aspnetcore.diagnostics.exceptions by exception type
        "Microsoft.AspNetCore.RateLimiting", // aspnetcore.rate_limiting.requests by result (rejections)
        "Microsoft.AspNetCore.Http.Connections", // SignalR connections
        "System.Net.Http", // outgoing calls (payment provider, storage)
        "System.Runtime", // GC, thread pool, CPU, memory
    ];

    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder)
    {
        var version = typeof(TelemetrySetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing
                .SetSampler(provider =>
                {
                    var settings = TelemetrySettings.From(provider.GetRequiredService<IConfiguration>());
                    if (settings.Enabled)
                    {
                        UseTraceContextOnly();
                    }

                    return settings.CreateSampler();
                })
                .AddSource(AppTelemetry.Name)
                // Npgsql's own spans (db.query.text is the parameterised SQL; parameter values are never recorded).
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Liveness probes come every few seconds and say nothing; readiness (uptime monitor) stays.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase);
                    // An exception event would carry the message, which may quote personal data.
                    options.RecordException = false;
                })
                .AddHttpClientInstrumentation(options => options.RecordException = false)
                // Before any exporter: what leaves the process has been scrubbed (DEC-551).
                .AddProcessor(new TelemetryScrubber())
                .Deferred((provider, tracer) =>
                {
                    var settings = TelemetrySettings.From(provider.GetRequiredService<IConfiguration>());
                    if (settings.Exporter("v1/traces") is { } exporter)
                    {
                        tracer.AddProcessor(new BatchActivityExportProcessor(new OtlpTraceExporter(exporter)));
                    }
                }))
            .WithMetrics(metrics => metrics
                .AddMeter(AppTelemetry.Name)
                .AddMeter(FrameworkMeters)
                .Deferred((provider, meter) =>
                {
                    var settings = TelemetrySettings.From(provider.GetRequiredService<IConfiguration>());
                    if (settings.Exporter("v1/metrics") is { } exporter)
                    {
                        meter.AddReader(new PeriodicExportingMetricReader(new OtlpMetricExporter(exporter), (int)settings.MetricInterval.TotalMilliseconds));
                    }
                }));

        builder.Services.AddHostedService<HealthMetrics>();
        return builder;
    }

    /// <summary>
    /// DEC-553: only the W3C trace context crosses process boundaries; a caller's baggage is neither read nor passed on to
    /// the payment provider or any other outgoing call. The propagators are process-wide, so they are changed only when
    /// telemetry is on (once set they stay: trace context only is also safe for a later host in the same process).
    /// </summary>
    private static void UseTraceContextOnly()
    {
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        if (DistributedContextPropagator.Current is not TraceContextOnlyPropagator)
        {
            DistributedContextPropagator.Current = new TraceContextOnlyPropagator(DistributedContextPropagator.CreateDefaultPropagator());
        }
    }

    /// <summary>Configures the provider once the service provider exists (the exporter depends on configuration read then).</summary>
    private static TracerProviderBuilder Deferred(this TracerProviderBuilder tracing, Action<IServiceProvider, TracerProviderBuilder> configure) =>
        tracing is IDeferredTracerProviderBuilder deferred
            ? deferred.Configure(configure)
            : throw new InvalidOperationException("The tracer provider builder cannot be configured late.");

    private static MeterProviderBuilder Deferred(this MeterProviderBuilder metrics, Action<IServiceProvider, MeterProviderBuilder> configure) =>
        metrics is IDeferredMeterProviderBuilder deferred
            ? deferred.Configure(configure)
            : throw new InvalidOperationException("The meter provider builder cannot be configured late.");
}

/// <summary>
/// The <c>OpenTelemetry</c> section, with the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> and
/// <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> environment variables as fallbacks. An invalid value never stops the API: telemetry
/// is then off and <see cref="Problem"/> says why (logged as a warning at start).
/// </summary>
internal sealed record TelemetrySettings(
    Uri? Endpoint,
    OtlpExportProtocol Protocol,
    bool Enabled,
    double SampleRatio,
    TimeSpan MetricInterval,
    TimeSpan HealthInterval,
    string? Problem = null)
{
    public static TelemetrySettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection("OpenTelemetry");
        var metricInterval = section.GetValue("Metrics:ExportInterval", TimeSpan.FromSeconds(15));
        var healthInterval = section.GetValue("Metrics:HealthInterval", TimeSpan.FromSeconds(30));
        TelemetrySettings Off(string problem) => new(null, OtlpExportProtocol.Grpc, false, 0, metricInterval, healthInterval, problem);

        var endpointText = FirstNonEmpty(section["Otlp:Endpoint"], configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Uri? endpoint = null;
        if (endpointText is not null && (!Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https")))
        {
            return Off("OpenTelemetry:Otlp:Endpoint (or OTEL_EXPORTER_OTLP_ENDPOINT) is not an absolute http(s) URL");
        }

        OtlpExportProtocol protocol;
        switch (FirstNonEmpty(section["Otlp:Protocol"], configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]))
        {
            case null or "grpc":
                protocol = OtlpExportProtocol.Grpc;
                break;
            case "http/protobuf":
                protocol = OtlpExportProtocol.HttpProtobuf;
                break;
            default:
                return Off("OpenTelemetry:Otlp:Protocol (or OTEL_EXPORTER_OTLP_PROTOCOL) is neither grpc nor http/protobuf");
        }

        var ratio = section.GetValue("Tracing:SampleRatio", 1.0);
        if (ratio is < 0 or > 1)
        {
            return Off("OpenTelemetry:Tracing:SampleRatio is not between 0 and 1");
        }

        return new TelemetrySettings(endpoint, protocol, endpoint is not null || section.GetValue("Enabled", false), ratio, metricInterval, healthInterval);
    }

    public Sampler CreateSampler() => Enabled ? new ApiSampler(SampleRatio) : new AlwaysOffSampler();

    /// <summary>
    /// The exporter options for one signal, or <c>null</c> without an endpoint. OTLP/HTTP needs the signal's path, appended
    /// to the endpoint's own path (<c>http://host:4318/otlp</c> → <c>http://host:4318/otlp/v1/traces</c>).
    /// </summary>
    public OtlpExporterOptions? Exporter(string signalPath) => Endpoint is null
        ? null
        : new OtlpExporterOptions
        {
            Protocol = Protocol,
            Endpoint = Protocol == OtlpExportProtocol.HttpProtobuf ? new Uri(WithTrailingSlash(Endpoint), signalPath) : Endpoint,
        };

    private static Uri WithTrailingSlash(Uri uri) =>
        uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// DEC-551: a request's (and a job's) spans follow the root's decision, taken here by trace id ratio. A remote caller's
/// "sampled" flag is not trusted (anyone on the internet can send one), nor its trace id (which it could choose to fall
/// under the ratio): a request with a remote parent is sampled at random with the same ratio. Root client spans, i.e. database or HTTP calls
/// made outside any request, job or outbox delivery (Hangfire's and the health publisher's polling), are dropped: they are
/// noise, every few seconds.
/// </summary>
internal sealed class ApiSampler(double ratio) : Sampler
{
    private readonly TraceIdRatioBasedSampler _root = new(ratio);

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var parent = samplingParameters.ParentContext;
        if (parent.TraceId == default)
        {
            return samplingParameters.Kind == ActivityKind.Client
                ? new SamplingResult(SamplingDecision.Drop)
                : _root.ShouldSample(samplingParameters);
        }

        if (parent.IsRemote)
        {
#pragma warning disable CA5394 // Sampling, not security: an unpredictable decision is the point.
            return new SamplingResult(ratio >= 1 || Random.Shared.NextDouble() < ratio ? SamplingDecision.RecordAndSample : SamplingDecision.Drop);
#pragma warning restore CA5394
        }

        return new SamplingResult((parent.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? SamplingDecision.RecordAndSample : SamplingDecision.Drop);
    }
}

/// <summary>Passes on <c>traceparent</c> and <c>tracestate</c> only; baggage is neither extracted nor injected (DEC-553).</summary>
internal sealed class TraceContextOnlyPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    private static readonly string[] TraceFields = ["traceparent", "tracestate"];

    public override IReadOnlyCollection<string> Fields => TraceFields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null)
        {
            return;
        }

        inner.Inject(activity, carrier, (c, name, value) =>
        {
            if (TraceFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                setter(c, name, value);
            }
        });
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}

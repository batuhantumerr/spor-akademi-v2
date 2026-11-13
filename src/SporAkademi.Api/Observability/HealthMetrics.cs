using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Application.Abstractions.Observability;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P4-07 (DEC-552): the readiness checks as gauges, so dashboards and alerts see health and the outbox backlog without
/// polling <c>/health/ready</c>. Runs the same checks as <c>/health/ready</c> every
/// <c>OpenTelemetry:Metrics:HealthInterval</c> (30 s) and only while telemetry is on; otherwise it returns at once. The
/// gauges report the last result: <c>spor_akademi.health.status</c> per check (2 healthy, 1 degraded, 0 unhealthy) and
/// the outbox counts from its check's data.
/// </summary>
internal sealed partial class HealthMetrics(
    HealthCheckService healthChecks,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<HealthMetrics> logger) : BackgroundService
{
    private static volatile HealthReport? _last;

    static HealthMetrics()
    {
        AppTelemetry.Meter.CreateObservableGauge(
            "spor_akademi.health.status",
            () => _last is { } report
                ? report.Entries.Select(e => new Measurement<int>(Score(e.Value.Status), new KeyValuePair<string, object?>("check", e.Key)))
                    .Append(new Measurement<int>(Score(report.Status), new KeyValuePair<string, object?>("check", "overall")))
                : [],
            "{status}",
            "Readiness per check: 2 healthy, 1 degraded, 0 unhealthy.");
        AppTelemetry.Meter.CreateObservableGauge(
            "spor_akademi.outbox.pending", () => OutboxValue("pending"), "{message}", "Outbox messages waiting for delivery.");
        AppTelemetry.Meter.CreateObservableGauge(
            "spor_akademi.outbox.dead_lettered", () => OutboxValue("deadLettered"), "{message}", "Dead-lettered outbox messages.");
        AppTelemetry.Meter.CreateObservableGauge(
            "spor_akademi.outbox.overdue", () => OutboxValue("overdueSeconds"), "s", "How long the oldest due outbox message has waited.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = TelemetrySettings.From(configuration);
        if (settings.Problem is { } problem)
        {
            // The setting's name only, never its value (an endpoint may carry credentials).
            LogTelemetryOff(logger, problem);
        }

        if (!settings.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _last = await healthChecks.CheckHealthAsync(check => check.Tags.Contains(HealthEndpoints.ReadyTag), stoppingToken);
                await Task.Delay(settings.HealthInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failing check run must not stop the host; the next run retries.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogCheckFailed(logger, exception.GetType().Name);
                await Task.Delay(settings.HealthInterval, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static int Score(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => 2,
        HealthStatus.Degraded => 1,
        _ => 0,
    };

    private static IEnumerable<Measurement<long>> OutboxValue(string key) =>
        _last?.Entries.TryGetValue("outbox", out var outbox) == true && outbox.Data.TryGetValue(key, out var value) && value is int or long
            ? [new Measurement<long>(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture))]
            : [];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry is off: {Problem}")]
    private static partial void LogTelemetryOff(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health metrics run failed with {Error}")]
    private static partial void LogCheckFailed(ILogger logger, string error);
}

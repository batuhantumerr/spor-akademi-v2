using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>
/// Degraded (still ready: the outbox keeps messages while the broker is away) when a configured broker cannot be reached;
/// Healthy when it is reachable or not configured (in-process delivery).
/// </summary>
internal sealed class RabbitMqHealthCheck(RabbitMqBroker broker, IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsConfigured)
        {
            return HealthCheckResult.Healthy("RabbitMQ is not configured; the outbox delivers in process.");
        }

        return await broker.IsReachableAsync(cancellationToken)
            ? HealthCheckResult.Healthy("RabbitMQ is reachable.")
            : HealthCheckResult.Degraded("RabbitMQ is unreachable; outbox messages wait.");
    }
}

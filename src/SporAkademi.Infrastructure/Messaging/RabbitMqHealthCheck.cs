using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>
/// Degraded (still ready: the outbox keeps messages while the broker is away) when a configured broker cannot be reached;
/// Healthy when it is reachable or not configured (in-process delivery).
/// </summary>
internal sealed class RabbitMqHealthCheck(RabbitMqBroker broker, ConsumerState consumers, IOptions<RabbitMqOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsConfigured)
        {
            return HealthCheckResult.Healthy("RabbitMQ is not configured; the outbox delivers in process.");
        }

        if (!await broker.IsReachableAsync(cancellationToken))
        {
            return HealthCheckResult.Degraded("RabbitMQ is unreachable; outbox messages wait.");
        }

        // Reachable, but this process's consumers are not all running (a channel the server closed, or still starting).
        return consumers.Running < consumers.Expected
            ? HealthCheckResult.Degraded("RabbitMQ is reachable but not all consumers are running.", data: new Dictionary<string, object>
            {
                ["expectedConsumers"] = consumers.Expected,
                ["runningConsumers"] = consumers.Running,
            })
            : HealthCheckResult.Healthy("RabbitMQ is reachable.");
    }
}

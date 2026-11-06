using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Degraded (still ready, so traffic keeps flowing) while dead-lettered messages exist or the oldest pending
/// message waits longer than <see cref="OutboxOptions.PendingAgeWarning"/>. The data holds counts only.
/// </summary>
internal sealed class OutboxHealthCheck(AppDbContext dbContext, TimeProvider clock, IOptions<OutboxOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var messages = dbContext.Set<OutboxMessage>().AsNoTracking();
        var deadLettered = await messages.CountAsync(m => m.DeadLetteredAt != null, cancellationToken);
        var pending = messages.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null);
        var pendingCount = await pending.CountAsync(cancellationToken);
        var oldestPending = await pending.MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);

        var oldestAge = oldestPending is { } oldest ? clock.GetUtcNow() - oldest : TimeSpan.Zero;
        var data = new Dictionary<string, object>
        {
            ["pending"] = pendingCount,
            ["deadLettered"] = deadLettered,
            ["oldestPendingAgeSeconds"] = (long)Math.Max(0, oldestAge.TotalSeconds),
        };

        if (deadLettered > 0)
        {
            return HealthCheckResult.Degraded($"{deadLettered} outbox messages are dead-lettered.", data: data);
        }

        return oldestAge > options.Value.PendingAgeWarning
            ? HealthCheckResult.Degraded("The oldest pending outbox message is overdue.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}

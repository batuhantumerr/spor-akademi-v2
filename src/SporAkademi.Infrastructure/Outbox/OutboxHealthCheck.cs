using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Degraded (still ready, so traffic keeps flowing) while dead-lettered messages exist or a pending message has been
/// due (<c>next_attempt_at</c>) for longer than <see cref="OutboxOptions.PendingAgeWarning"/>, i.e. nothing is
/// processing it; a message waiting for its next retry is not overdue. The data holds counts only.
/// </summary>
internal sealed class OutboxHealthCheck(AppDbContext dbContext, TimeProvider clock, IOptions<OutboxOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var messages = dbContext.Set<OutboxMessage>().AsNoTracking();
        var deadLettered = await messages.CountAsync(m => m.DeadLetteredAt != null, cancellationToken);
        var pending = messages.Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null);
        var pendingCount = await pending.CountAsync(cancellationToken);
        var earliestDue = await pending.MinAsync(m => (DateTimeOffset?)m.NextAttemptAt, cancellationToken);

        var overdue = earliestDue is { } due ? clock.GetUtcNow() - due : TimeSpan.Zero;
        var data = new Dictionary<string, object>
        {
            ["pending"] = pendingCount,
            ["deadLettered"] = deadLettered,
            ["overdueSeconds"] = (long)Math.Max(0, overdue.TotalSeconds),
        };

        if (deadLettered > 0)
        {
            return HealthCheckResult.Degraded($"{deadLettered} outbox messages are dead-lettered.", data: data);
        }

        return overdue > options.Value.PendingAgeWarning
            ? HealthCheckResult.Degraded("A due outbox message has not been processed.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// DEC-375: Degraded (still ready) when, after the <see cref="GraceDays"/>th day of the current month (UTC), a school with
/// a fee rules version in force for the previous month still has that month open, i.e. the daily close job keeps failing
/// for it and its past month still follows today's guardian links. One query over all schools; the data holds a count
/// only. Raw SQL, because a health check has no school and the tenant filters would hide every row.
/// </summary>
internal sealed class BillingCloseHealthCheck(AppDbContext dbContext, TimeProvider clock) : IHealthCheck
{
    /// <summary>The close job runs daily; two days give it a few runs (and a time zone margin) before warning.</summary>
    public const int GraceDays = 2;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (today.Day <= GraceDays)
        {
            return HealthCheckResult.Healthy();
        }

        var previous = YearMonth.From(today.AddMonths(-1)).ToString();
        var open = await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT count(DISTINCT r.tenant_id)::int AS "Value"
                FROM billing.billing_rules r
                WHERE r.effective_from <= {previous}
                  AND NOT EXISTS (
                      SELECT 1 FROM billing.closed_periods c
                      WHERE c.tenant_id = r.tenant_id AND c.period = {previous})
                """)
            .SingleAsync(cancellationToken);

        var data = new Dictionary<string, object> { ["schoolsWithOpenPeriod"] = open, ["period"] = previous };
        return open > 0
            ? HealthCheckResult.Degraded($"{open} schools have not closed {previous} under their fee rules.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}

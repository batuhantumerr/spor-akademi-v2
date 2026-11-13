using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// DEC-375: Degraded (still ready) when, after the <see cref="GraceDays"/>th day of the current month (UTC), a school with
/// a fee rules version in force for the previous month still has that month open, i.e. the daily close job keeps failing
/// for it and its past month still follows today's guardian links. One query over all schools; the data holds a count
/// only. A health check has no school, so the tenant filters and row level security hide every row; the count comes
/// from the <c>billing.count_schools_with_open_period</c> function, a narrow read across schools (P4-04, DEC-523).
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
        var open = await dbContext.Database
            .SqlQuery<int>($"SELECT billing.count_schools_with_open_period({previous}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        var data = new Dictionary<string, object> { ["schoolsWithOpenPeriod"] = open, ["period"] = previous };
        return open > 0
            ? HealthCheckResult.Degraded($"{open} schools have not closed {previous} under their fee rules.", data: data)
            : HealthCheckResult.Healthy(data: data);
    }
}

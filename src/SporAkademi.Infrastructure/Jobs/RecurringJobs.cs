using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// The recurring jobs (DEC-333). A job's arguments are stored in the jobs schema and shown in the panel, so jobs
/// take no arguments beyond a cancellation token (ids at most, never personal data). A failed run is not retried:
/// the next occurrence runs anyway, and the failure stays visible in the panel.
/// </summary>
internal static class RecurringJobs
{
    public static void Register(IRecurringJobManager manager, JobsOptions jobs, OutboxOptions outbox)
    {
        var utc = new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc };
        if (outbox.Scheduler == OutboxScheduler.Hangfire)
        {
            manager.AddOrUpdate<OutboxJobs>(OutboxJobs.ProcessJobId, job => job.ProcessAsync(CancellationToken.None), jobs.OutboxCron, utc);
            manager.AddOrUpdate<OutboxJobs>(OutboxJobs.CleanupJobId, job => job.CleanupAsync(CancellationToken.None), jobs.OutboxCleanupCron, utc);
        }
        else
        {
            // Another scheduler (or none) drives the outbox: stale registrations in the shared storage must not run.
            manager.RemoveIfExists(OutboxJobs.ProcessJobId);
            manager.RemoveIfExists(OutboxJobs.CleanupJobId);
        }

        manager.AddOrUpdate<RefreshTokenCleanupJob>(RefreshTokenCleanupJob.JobId, job => job.RunAsync(CancellationToken.None), jobs.RefreshTokenCleanupCron, utc);
    }
}

/// <summary>Drives <see cref="OutboxProcessor"/> from Hangfire (DEC-302, DEC-333); the processor itself is unchanged.</summary>
internal sealed class OutboxJobs(OutboxProcessor processor, IOptions<OutboxOptions> outbox, IOptions<JobsOptions> jobs)
{
    public const string ProcessJobId = "outbox-process";
    public const string CleanupJobId = "outbox-cleanup";

    /// <summary>Delivers batches until one is not full (or the per-run cap is reached).</summary>
    /// <returns>How many messages were claimed.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Outbox: deliver due messages")]
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < jobs.Value.MaxOutboxBatchesPerRun && !cancellationToken.IsCancellationRequested; batch++)
        {
            var claimed = await processor.ProcessBatchAsync(cancellationToken);
            total += claimed;
            if (claimed < outbox.Value.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Outbox: delete processed messages past retention")]
    public Task<int> CleanupAsync(CancellationToken cancellationToken) => processor.CleanupAsync(cancellationToken);
}

/// <summary>
/// Deletes refresh tokens whose session (token family) has ended: they can never be used or reused again (the same
/// rule <c>User</c> applies to its own tokens at login, which only helps users who log in again). School users are
/// handled per school (DEC-334), Platform Admins on their own.
/// </summary>
internal sealed partial class RefreshTokenCleanupJob(
    TenantJobRunner runner,
    TimeProvider clock,
    ILogger<RefreshTokenCleanupJob> logger)
{
    public const string JobId = "identity-refresh-token-cleanup";

    /// <returns>The number of deleted tokens.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Identity: delete refresh tokens of ended sessions")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var deleted = 0;
        await runner.ForEachSchoolAsync(
            JobId,
            async (services, school, ct) => deleted += await DeleteAsync(services.GetRequiredService<AppDbContext>(), school, now, ct),
            cancellationToken);
        deleted += await runner.ForPlatformAsync(
            (services, ct) => DeleteAsync(services.GetRequiredService<AppDbContext>(), null, now, ct),
            cancellationToken);

        if (deleted > 0)
        {
            LogDeleted(logger, deleted);
        }

        return deleted;
    }

    /// <summary>Users are not tenant-filtered (DEC-105), so the school is stated explicitly.</summary>
    private static Task<int> DeleteAsync(AppDbContext db, SchoolId? school, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.Set<RefreshToken>()
            .Where(t => t.FamilyExpiresAt <= now && db.Set<User>().Any(u => u.Id == t.UserId && u.TenantId == school))
            .ExecuteDeleteAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} refresh tokens of ended sessions")]
    private static partial void LogDeleted(ILogger logger, int count);
}

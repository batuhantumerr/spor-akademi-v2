using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing;
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
        manager.AddOrUpdate<StudentAnonymizationJob>(StudentAnonymizationJob.JobId, job => job.RunAsync(CancellationToken.None), jobs.StudentAnonymizationCron, utc);
        manager.AddOrUpdate<DuesReminderJob>(DuesReminderJob.JobId, job => job.RunAsync(CancellationToken.None), jobs.DuesReminderCron, utc);
    }
}

/// <summary>
/// Drives <see cref="OutboxProcessor"/> from Hangfire (DEC-302, DEC-333); the processor itself is unchanged. Hangfire
/// starts a run every interval even if the previous one is still delivering, so a run first takes a session-level
/// advisory lock without waiting; when another run (in any API instance) holds it, this one does nothing.
/// <c>SKIP LOCKED</c> alone already prevents double delivery; the lock keeps runs from piling up.
/// </summary>
internal sealed partial class OutboxJobs(
    IServiceScopeFactory scopeFactory,
    OutboxProcessor processor,
    IOptions<OutboxOptions> outbox,
    IOptions<JobsOptions> jobs,
    ILogger<OutboxJobs> logger)
{
    public const string ProcessJobId = "outbox-process";
    public const string CleanupJobId = "outbox-cleanup";

    /// <summary>Advisory lock key of the delivery run ("SAOUTBOX" as bytes).</summary>
    internal const long ProcessLockKey = 0x5341_4F55_5442_4F58;

    private const string TryLockSql = "SELECT pg_try_advisory_lock(5999163406538657624)";

    /// <summary>Delivers batches until one is not full (or the per-run cap is reached).</summary>
    /// <returns>How many messages were claimed; 0 when another run is in progress.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Outbox: deliver due messages")]
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = TryLockSql;
            var acquired = await command.ExecuteScalarAsync(cancellationToken) is true;
            if (!acquired)
            {
                LogSkipped(logger);
                return 0;
            }

            try
            {
                return await DeliverAsync(cancellationToken);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({ProcessLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Outbox: delete processed messages past retention")]
    public Task<int> CleanupAsync(CancellationToken cancellationToken) => processor.CleanupAsync(cancellationToken);

    private async Task<int> DeliverAsync(CancellationToken cancellationToken)
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox run skipped: another run is in progress")]
    private static partial void LogSkipped(ILogger logger);
}

/// <summary>
/// Deletes refresh tokens whose session (token family) has ended: they can never be used or reused again (the same
/// rule <c>User</c> applies to its own tokens at login, which only helps users who log in again), and expired
/// set-password links (P2-F4, DEC-352). School users are handled per school (DEC-334), Platform Admins on their own.
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
    private static async Task<int> DeleteAsync(AppDbContext db, SchoolId? school, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Set<RefreshToken>()
            .Where(t => t.FamilyExpiresAt <= now && db.Set<User>().Any(u => u.Id == t.UserId && u.TenantId == school))
            .ExecuteDeleteAsync(cancellationToken)
        + await db.Set<PasswordToken>()
            .Where(t => t.ExpiresAt <= now && db.Set<User>().Any(u => u.Id == t.UserId && u.TenantId == school))
            .ExecuteDeleteAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} refresh tokens of ended sessions and expired set-password links")]
    private static partial void LogDeleted(ILogger logger, int count);
}

/// <summary>
/// P2-F4 (DEC-355): once a day, per school (DEC-334), records the fee reminders that are due and queues their e-mails
/// through the outbox. Re-running is harmless: a reminder exists at most once per enrollment, period and kind.
/// </summary>
internal sealed partial class DuesReminderJob(TenantJobRunner runner, ILogger<DuesReminderJob> logger)
{
    public const string JobId = "billing-dues-reminders";

    /// <returns>The number of reminders recorded.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Billing: record and queue fee reminders")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var recorded = 0;
        await runner.ForEachSchoolAsync(
            JobId,
            async (services, _, ct) =>
            {
                var result = await services.GetRequiredService<ICommandHandler<SendDuesRemindersCommand, int>>()
                    .Handle(new SendDuesRemindersCommand(), ct);
                recorded += result.IsSuccess
                    ? result.Value
                    : throw new InvalidOperationException($"Recording fee reminders failed with {result.Error.Code}.");
            },
            cancellationToken);

        if (recorded > 0)
        {
            LogRecorded(logger, recorded);
        }

        return recorded;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Count} fee reminders")]
    private static partial void LogRecorded(ILogger logger, int count);
}

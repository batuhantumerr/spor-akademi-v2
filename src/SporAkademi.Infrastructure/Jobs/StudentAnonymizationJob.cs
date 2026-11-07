using System.Diagnostics;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// P2-F5 (DEC-008, DEC-362): every day, per school, anonymizes the students who left before the school's retention
/// period, then deletes guardians no student is linked to (DEC-368). Each batch runs in its own scope and
/// transaction; each school's run is stored as an <see cref="AnonymizationRun"/> and logged with counts and ids only
/// (NFR-PRV-03, NFR-PRV-04). Running it twice changes nothing the second time: anonymized students are no longer due.
/// Two runs never overlap (a session-level advisory lock, like the outbox run): a run that finds another in progress
/// does nothing.
/// </summary>
internal sealed partial class StudentAnonymizationJob(
    TenantJobRunner runner,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<JobsOptions> options,
    ILogger<StudentAnonymizationJob> logger)
{
    public const string JobId = "students-anonymization";

    /// <summary>Advisory lock key of a run ("SAANONYM" as bytes).</summary>
    internal const long RunLockKey = 0x5341_414E_4F4E_594D;

    /// <summary>At most this many unlinked guardians are deleted per school and run; the rest wait for the next night.</summary>
    private const int UnlinkedGuardiansPerRun = 1_000;

    /// <returns>The number of anonymized students across all schools; 0 when another run is in progress.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Students: anonymize departed students past the retention period")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var acquired = await db.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_lock({RunLockKey}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!acquired)
            {
                LogOverlap(logger);
                return 0;
            }

            try
            {
                var total = 0;
                await runner.ForEachSchoolAsync(
                    JobId,
                    async (services, school, ct) => total += await RunForSchoolAsync(services, school, ct),
                    cancellationToken);
                return total;
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({RunLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<int> RunForSchoolAsync(IServiceProvider services, SchoolId school, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        var anonymizer = services.GetRequiredService<IStudentAnonymizer>();
        var plan = await anonymizer.PlanAsync(cancellationToken);
        var batchSize = options.Value.AnonymizationBatchSize;
        var anonymized = 0;
        var skipped = 0;
        // Ids a batch returned but did not anonymize; excluded from the next batches so the run always ends.
        var leftOver = new HashSet<StudentId>();

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var due = await anonymizer.NextDueAsync(plan, leftOver, batchSize, cancellationToken);
                if (due.Count == 0)
                {
                    break;
                }

                // A fresh scope per batch: its DbContext tracks only this batch's students.
                var batch = await runner.ForSchoolAsync(
                    school,
                    (batchServices, ct) => batchServices.GetRequiredService<IStudentAnonymizer>().AnonymizeAsync(plan, due, ct),
                    cancellationToken);
                anonymized += batch.Anonymized.Count;
                skipped += batch.Skipped.Count;
                leftOver.UnionWith(due.Except(batch.Anonymized));
                foreach (var student in batch.Skipped)
                {
                    LogSkipped(logger, school.Value, student.Value);
                }

                if (due.Count < batchSize)
                {
                    break;
                }
            }

            var guardians = await runner.ForSchoolAsync(
                school,
                (sweepServices, ct) => sweepServices.GetRequiredService<IStudentAnonymizer>().DeleteUnlinkedGuardiansAsync(UnlinkedGuardiansPerRun, ct),
                cancellationToken);
            if (guardians > 0)
            {
                LogGuardians(logger, school.Value, guardians);
            }
        }
        catch (Exception exception)
        {
            // Cancellation (shutdown) is recorded as a failed run too: the work done so far is committed and counted.
            await TryRecordFailureAsync(school, plan, startedAt, started, anonymized, skipped, exception);
            throw;
        }

        var durationMs = await RecordAsync(school, plan, startedAt, started, anonymized, skipped, AnonymizationRunStatus.Succeeded, cancellationToken);
        LogRun(logger, school.Value, anonymized, skipped, plan.Cutoff, plan.PersonalDataMonths, durationMs);
        return anonymized;
    }

    /// <summary>A failure to record the failure must not hide the original exception; only its type is logged.</summary>
    private async Task TryRecordFailureAsync(
        SchoolId school,
        AnonymizationPlan plan,
        DateTimeOffset startedAt,
        long started,
        int anonymized,
        int skipped,
        Exception original)
    {
        try
        {
            await RecordAsync(school, plan, startedAt, started, anonymized, skipped, AnonymizationRunStatus.Failed, CancellationToken.None);
        }
#pragma warning disable CA1031 // The original exception is rethrown by the caller; this one is only logged by type.
        catch (Exception recording)
#pragma warning restore CA1031
        {
            LogRecordFailed(logger, school.Value, original.GetType().FullName ?? original.GetType().Name, recording.GetType().FullName ?? recording.GetType().Name);
        }
    }

    /// <summary>The run record goes in its own scope, so a failed batch's state cannot leak into it.</summary>
    private async Task<long> RecordAsync(
        SchoolId school,
        AnonymizationPlan plan,
        DateTimeOffset startedAt,
        long started,
        int anonymized,
        int skipped,
        AnonymizationRunStatus status,
        CancellationToken cancellationToken)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var run = AnonymizationRun.Record(school, startedAt, durationMs, plan.Cutoff, plan.PersonalDataMonths, anonymized, skipped, status);
        await runner.ForSchoolAsync(
            school,
            async (services, ct) =>
            {
                await services.GetRequiredService<IStudentAnonymizer>().RecordRunAsync(run, ct);
                return 0;
            },
            cancellationToken);
        return durationMs;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Anonymization for school {SchoolId}: {Anonymized} anonymized, {Skipped} skipped, cutoff {Cutoff} ({Months} months), {DurationMs} ms")]
    private static partial void LogRun(ILogger logger, Guid schoolId, int anonymized, int skipped, DateOnly cutoff, int months, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anonymization of student {StudentId} in school {SchoolId} was skipped after a concurrent change; the next run retries it")]
    private static partial void LogSkipped(ILogger logger, Guid schoolId, Guid studentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} guardians without students in school {SchoolId}")]
    private static partial void LogGuardians(ILogger logger, Guid schoolId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Anonymization run of school {SchoolId} failed with {Error} and could not be recorded ({RecordError})")]
    private static partial void LogRecordFailed(ILogger logger, Guid schoolId, string error, string recordError);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anonymization run skipped: another run is in progress")]
    private static partial void LogOverlap(ILogger logger);
}

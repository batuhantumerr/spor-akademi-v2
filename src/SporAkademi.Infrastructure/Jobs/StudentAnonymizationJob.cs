using System.Diagnostics;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// P2-F5 (DEC-008, DEC-362): every day, per school, anonymizes the students who left before the school's retention
/// period. Each batch runs in its own scope and transaction; each school's run is stored as an
/// <see cref="AnonymizationRun"/> and logged with counts and ids only (NFR-PRV-03, NFR-PRV-04). Running it twice
/// changes nothing the second time: anonymized students are no longer due.
/// </summary>
internal sealed partial class StudentAnonymizationJob(
    TenantJobRunner runner,
    TimeProvider clock,
    IOptions<JobsOptions> options,
    ILogger<StudentAnonymizationJob> logger)
{
    public const string JobId = "students-anonymization";

    /// <returns>The number of anonymized students across all schools.</returns>
    [AutomaticRetry(Attempts = 0)]
    [JobDisplayName("Students: anonymize departed students past the retention period")]
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        await runner.ForEachSchoolAsync(
            JobId,
            async (services, school, ct) => total += await RunForSchoolAsync(services, school, ct),
            cancellationToken);
        return total;
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
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordAsync(school, plan, startedAt, started, anonymized, skipped, AnonymizationRunStatus.Failed, CancellationToken.None);
            throw;
        }

        var durationMs = await RecordAsync(school, plan, startedAt, started, anonymized, skipped, AnonymizationRunStatus.Succeeded, cancellationToken);
        LogRun(logger, school.Value, anonymized, skipped, plan.Cutoff, plan.PersonalDataMonths, durationMs);
        return anonymized;
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
}

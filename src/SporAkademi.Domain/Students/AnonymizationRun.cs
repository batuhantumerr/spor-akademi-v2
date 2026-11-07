using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Students;

public readonly record struct AnonymizationRunId(Guid Value) : IEntityId<AnonymizationRunId>
{
    public static AnonymizationRunId From(Guid value) => new(value);
}

public enum AnonymizationRunStatus
{
    Succeeded,
    Failed,
}

/// <summary>
/// One run of the anonymization job for one school (P2-F5, DEC-364): the proof that the retention policy is applied
/// ("her çalışma loglanır"). Counts, dates and durations only; never names or student ids, so the record itself is
/// not personal data and is kept after the students it counts are gone.
/// </summary>
public sealed class AnonymizationRun : AggregateRoot<AnonymizationRunId>, ITenantScoped
{
    private AnonymizationRun(
        AnonymizationRunId id,
        SchoolId tenantId,
        DateTimeOffset startedAt,
        long durationMs,
        DateOnly cutoff,
        int personalDataMonths,
        int anonymizedCount,
        int skippedCount,
        AnonymizationRunStatus status)
        : base(id)
    {
        TenantId = tenantId;
        StartedAt = startedAt;
        DurationMs = durationMs;
        Cutoff = cutoff;
        PersonalDataMonths = personalDataMonths;
        AnonymizedCount = anonymizedCount;
        SkippedCount = skippedCount;
        Status = status;
    }

    // For EF Core materialization only.
    private AnonymizationRun()
        : base(default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Wall time of the run, measured with a monotonic clock.</summary>
    public long DurationMs { get; private set; }

    /// <summary>Students who left on or before this date were due.</summary>
    public DateOnly Cutoff { get; private set; }

    /// <summary>The school's retention period the run applied.</summary>
    public int PersonalDataMonths { get; private set; }

    public int AnonymizedCount { get; private set; }

    /// <summary>Due students left for the next run, e.g. because an admin changed them at the same moment.</summary>
    public int SkippedCount { get; private set; }

    public AnonymizationRunStatus Status { get; private set; }

    public static AnonymizationRun Record(
        SchoolId tenantId,
        DateTimeOffset startedAt,
        long durationMs,
        DateOnly cutoff,
        int personalDataMonths,
        int anonymizedCount,
        int skippedCount,
        AnonymizationRunStatus status)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);
        ArgumentOutOfRangeException.ThrowIfNegative(anonymizedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(skippedCount);
        return new AnonymizationRun(
            EntityId.New<AnonymizationRunId>(), tenantId, startedAt, durationMs, cutoff, personalDataMonths, anonymizedCount, skippedCount, status);
    }
}

using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>Frozen charges and closed periods of the current school (tenant filter applies). Never updated or deleted.</summary>
public interface IChargeSnapshotRepository
{
    /// <summary>Closed periods from <paramref name="firstPeriod"/> to <paramref name="lastPeriod"/> (inclusive).</summary>
    Task<IReadOnlySet<YearMonth>> ClosedPeriodsAsync(YearMonth firstPeriod, YearMonth lastPeriod, CancellationToken cancellationToken);

    /// <summary>Every snapshot of the school from <paramref name="firstPeriod"/> to <paramref name="lastPeriod"/> (inclusive).</summary>
    Task<IReadOnlyList<ChargeSnapshot>> ListAsync(YearMonth firstPeriod, YearMonth lastPeriod, CancellationToken cancellationToken);

    /// <summary>The snapshots of the given students' enrollments from <paramref name="firstPeriod"/> to <paramref name="lastPeriod"/> (one family, DEC-388).</summary>
    Task<IReadOnlyList<ChargeSnapshot>> ListOfStudentsBetweenAsync(
        IReadOnlyCollection<StudentId> studentIds,
        YearMonth firstPeriod,
        YearMonth lastPeriod,
        CancellationToken cancellationToken);

    /// <summary>The snapshots of the given students' enrollments for one period.</summary>
    Task<IReadOnlyList<ChargeSnapshot>> ListOfStudentsAsync(IReadOnlyCollection<StudentId> studentIds, YearMonth period, CancellationToken cancellationToken);

    void Add(ChargeSnapshot snapshot);

    void Add(ClosedPeriod period);
}

/// <summary>
/// DEC-375: closes the current school's periods before its current one, from the first fee rules version on, that are
/// not closed yet: every charge of such a period is frozen in a <see cref="ChargeSnapshot"/> and the period is recorded
/// as closed, in one transaction. Idempotent (a closed period is skipped; unique indexes stop a concurrent run).
/// Periods without a rules version need no snapshot: they are the group fees, which never change for a past month.
/// </summary>
public sealed record CloseBillingPeriodsCommand : ICommand<ClosedPeriodsSummary>;

/// <summary>What one run closed: periods and snapshots (counts only, for the job log).</summary>
public sealed record ClosedPeriodsSummary(int Periods, int Snapshots);

internal sealed class CloseBillingPeriodsHandler(
    DueCharges dueCharges,
    IBillingRulesRepository rules,
    IChargeSnapshotRepository snapshots,
    ITenancyModule tenancy,
    ICurrentUser currentUser,
    TimeProvider clock) : ICommandHandler<CloseBillingPeriodsCommand, ClosedPeriodsSummary>
{
    /// <summary>A run closes at most this many periods (two years); a longer backlog closes over the next runs.</summary>
    public const int MaxPeriodsPerRun = 24;

    public async Task<Result<ClosedPeriodsSummary>> Handle(CloseBillingPeriodsCommand command, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var versions = await rules.ListAsync(cancellationToken);
        if (versions.Count == 0)
        {
            return Result.Success(new ClosedPeriodsSummary(0, 0));
        }

        var currentPeriod = YearMonth.From(await tenancy.TodayAsync(tenant, cancellationToken));
        var first = versions.Min(v => v.EffectiveFrom)!;
        var last = YearMonth.From(currentPeriod.FirstDay.AddMonths(-1));
        if (first > last)
        {
            return Result.Success(new ClosedPeriodsSummary(0, 0));
        }

        var closed = await snapshots.ClosedPeriodsAsync(first, last, cancellationToken);
        var (periods, frozen) = (0, 0);
        for (var period = first; period <= last && periods < MaxPeriodsPerRun; period = period.Next())
        {
            if (closed.Contains(period))
            {
                continue;
            }

            var book = await dueCharges.ForSchoolAsync(period, period, cancellationToken);
            foreach (var charge in book.Charges)
            {
                snapshots.Add(ChargeSnapshot.Take(tenant, charge));
            }

            snapshots.Add(ClosedPeriod.Close(tenant, period, currentPeriod, clock.GetUtcNow(), book.Charges.Count));
            periods++;
            frozen += book.Charges.Count;
        }

        return Result.Success(new ClosedPeriodsSummary(periods, frozen));
    }
}

using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Reports;

/// <summary>A month of the range and the day its head counts are taken (its last day; today for the current month).</summary>
public sealed record ReportPeriodDto(string Period, DateOnly ReferenceDate);

/// <summary>
/// Enrollments active on the month's reference date against the capacity of the groups open that day; utilisation with
/// 4 decimals, null when no group was open (DEC-407).
/// </summary>
public sealed record OccupancyCellDto(string Period, int Enrolled, int Capacity, decimal? Utilisation);

/// <summary>A group, sport or coach (null id and empty name: groups without a coach) with its months.</summary>
public sealed record OccupancyRowDto(Guid? Id, string Name, IReadOnlyList<OccupancyCellDto> Months);

/// <summary>The occupancy trend (P3-09, DEC-407). <see cref="Total"/> sums every row per month.</summary>
public sealed record OccupancyReportDto(
    string From,
    string To,
    ReportDimension By,
    Guid? SportId,
    Guid? CoachId,
    IReadOnlyList<ReportPeriodDto> Periods,
    IReadOnlyList<OccupancyRowDto> Rows,
    OccupancyRowDto Total);

/// <summary>Monthly enrolled vs capacity per group, sport or coach, optionally of one sport's or coach's groups.</summary>
public sealed record GetOccupancyReportQuery(string? From, string? To, string? By, Guid? SportId, Guid? CoachId)
    : IQuery<OccupancyReportDto>;

internal sealed class GetOccupancyReportValidator : AbstractValidator<GetOccupancyReportQuery>
{
    public GetOccupancyReportValidator()
    {
        ReportInput.AddRangeRules(this, q => q.From, q => q.To);
        RuleFor(q => q.By).Must(ReportInput.IsDimension).WithErrorCode(CommonErrors.FieldInvalid.Code).OverridePropertyName("by");
    }
}

/// <summary>One group open on a month's reference date and its enrollments active that day.</summary>
public sealed record GroupMonthOccupancy(Guid GroupId, string Period, int Enrolled, int Capacity);

/// <summary>The cached part of the occupancy report (DEC-410).</summary>
public sealed record OccupancyFacts(IReadOnlyList<ReportGroup> Groups, IReadOnlyList<ReportPeriodDto> Periods, IReadOnlyList<GroupMonthOccupancy> Cells);

internal sealed class GetOccupancyReportHandler(
    IReportsReadModel readModel,
    ReportGroups groups,
    ReportFilters filters,
    ITenancyModule tenancy,
    ITenantCache cache,
    ICurrentUser currentUser) : IQueryHandler<GetOccupancyReportQuery, OccupancyReportDto>
{
    public async Task<Result<OccupancyReportDto>> Handle(GetOccupancyReportQuery query, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var today = await tenancy.TodayAsync(tenant, cancellationToken);
        var range = ReportRange.Resolve(query.From, query.To, YearMonth.From(today));
        if (range.IsFailure)
        {
            return Result.Failure<OccupancyReportDto>(range.Error);
        }

        if (await filters.CheckAsync(query.SportId, query.CoachId, cancellationToken) is { } notFound)
        {
            return Result.Failure<OccupancyReportDto>(notFound);
        }

        var (from, to) = (range.Value.From, range.Value.To);
        var facts = await cache.GetOrCreateAsync(
            TenantCacheKey.For(tenant, CacheArea.Reports, "occupancy", from.FirstDay, to.LastDay, today),
            async ct => await FactsAsync(range.Value, today, ct),
            cancellationToken);

        return Result.Success(Build(facts, range.Value, ReportInput.Dimension(query.By), query.SportId, query.CoachId));
    }

    private async Task<OccupancyFacts> FactsAsync(ReportRange range, DateOnly today, CancellationToken cancellationToken)
    {
        var all = await groups.ListAsync(cancellationToken);
        var periods = range.Periods().Select(p => new ReportPeriodDto(p.ToString(), ReportRange.ReferenceDate(p, today))).ToList();
        // Only enrollments active on some reference date matter: one query over the first to the last reference date.
        var spans = await readModel.ListEnrollmentSpansAsync(periods[0].ReferenceDate, periods[^1].ReferenceDate, cancellationToken);
        var spansOf = spans.ToLookup(s => s.GroupId);
        var currentPeriod = YearMonth.From(today).ToString();

        var cells = new List<GroupMonthOccupancy>();
        foreach (var period in periods)
        {
            foreach (var group in all.Where(g => IsOpen(g, period, currentPeriod)))
            {
                cells.Add(new GroupMonthOccupancy(group.Id, period.Period, spansOf[group.Id].Count(s => s.IsActiveOn(period.ReferenceDate)), group.Capacity));
            }
        }

        return new OccupancyFacts(all, periods, cells);
    }

    /// <summary>
    /// DEC-407: a group counts in a month when it was not archived by the reference date and had already started (its first
    /// enrollment began by then); an active group without enrollments counts in the current month only. Capacity history
    /// is not kept, so a group's current capacity stands for every month.
    /// </summary>
    private static bool IsOpen(ReportGroup group, ReportPeriodDto period, string currentPeriod) =>
        (group.ArchivedOn is not { } archived || archived > period.ReferenceDate)
        && (group.FirstEnrollmentStart is { } first
            ? first <= period.ReferenceDate
            : !group.IsArchived && period.Period == currentPeriod);

    internal static OccupancyReportDto Build(OccupancyFacts facts, ReportRange range, ReportDimension by, Guid? sportId, Guid? coachId)
    {
        var groupsById = facts.Groups.Where(g => ReportRows.Matches(g, sportId, coachId)).ToDictionary(g => g.Id);
        var cells = facts.Cells.Where(c => groupsById.ContainsKey(c.GroupId)).ToList();

        var rows = cells
            .GroupBy(c => ReportRows.KeyOf(groupsById[c.GroupId], by))
            .Select(row => Row(row.Key.Id, row.Key.Name, [.. row], facts.Periods));

        return new OccupancyReportDto(
            range.From.ToString(),
            range.To.ToString(),
            by,
            sportId,
            coachId,
            facts.Periods,
            [.. ReportRows.Ordered(rows, r => r.Id, r => r.Name)],
            Row(null, string.Empty, cells, facts.Periods));
    }

    private static OccupancyRowDto Row(Guid? id, string name, List<GroupMonthOccupancy> cells, IReadOnlyList<ReportPeriodDto> periods) =>
        new(id, name, [.. periods.Select(p => Cell(p.Period, cells.Where(c => c.Period == p.Period)))]);

    private static OccupancyCellDto Cell(string period, IEnumerable<GroupMonthOccupancy> cells)
    {
        var list = cells.ToList();
        var (enrolled, capacity) = (list.Sum(c => c.Enrolled), list.Sum(c => c.Capacity));
        return new OccupancyCellDto(period, enrolled, capacity, ReportRows.Ratio(enrolled, capacity));
    }
}

using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Reports;

/// <summary>
/// Attendance record counts of a month (or, with a null <see cref="Period"/>, of the whole range) and the rate
/// (present + late) / (present + late + absent) with 4 decimals; excused does not count (DEC-174). Null rate: nothing counted.
/// </summary>
public sealed record AttendanceCellDto(string? Period, int Present, int Late, int Absent, int Excused, decimal? Rate);

/// <summary>A group, sport or coach (null id and empty name: groups without a coach) with its months and range total.</summary>
public sealed record AttendanceRowDto(Guid? Id, string Name, IReadOnlyList<AttendanceCellDto> Months, AttendanceCellDto Total);

/// <summary>The attendance report (P3-09, DEC-406). <see cref="Total"/> sums every row.</summary>
public sealed record AttendanceReportDto(
    string From,
    string To,
    ReportDimension By,
    Guid? SportId,
    Guid? CoachId,
    IReadOnlyList<string> Periods,
    IReadOnlyList<AttendanceRowDto> Rows,
    AttendanceRowDto Total);

/// <summary>
/// Monthly attendance per group, sport or coach (<c>by</c>: <c>group</c> default, <c>sport</c>, <c>coach</c>), optionally only
/// the groups of one sport or coach (the drill-down). Months <c>YYYY-MM</c>; default the last six up to the current month.
/// </summary>
public sealed record GetAttendanceReportQuery(string? From, string? To, string? By, Guid? SportId, Guid? CoachId)
    : IQuery<AttendanceReportDto>;

internal sealed class GetAttendanceReportValidator : AbstractValidator<GetAttendanceReportQuery>
{
    public GetAttendanceReportValidator()
    {
        ReportInput.AddRangeRules(this, q => q.From, q => q.To);
        RuleFor(q => q.By).Must(ReportInput.IsDimension).WithErrorCode(CommonErrors.FieldInvalid.Code).OverridePropertyName("by");
    }
}

/// <summary>The cached part of the attendance report: every group and its counts per month of one range (DEC-410).</summary>
public sealed record AttendanceFacts(IReadOnlyList<ReportGroup> Groups, IReadOnlyList<GroupMonthAttendance> Counts);

internal sealed class GetAttendanceReportHandler(
    IReportsReadModel readModel,
    ReportGroups groups,
    ReportFilters filters,
    ITenancyModule tenancy,
    ITenantCache cache,
    ICurrentUser currentUser) : IQueryHandler<GetAttendanceReportQuery, AttendanceReportDto>
{
    public async Task<Result<AttendanceReportDto>> Handle(GetAttendanceReportQuery query, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var today = await tenancy.TodayAsync(tenant, cancellationToken);
        var range = ReportRange.Resolve(query.From, query.To, YearMonth.From(today));
        if (range.IsFailure)
        {
            return Result.Failure<AttendanceReportDto>(range.Error);
        }

        if (await filters.CheckAsync(query.SportId, query.CoachId, cancellationToken) is { } notFound)
        {
            return Result.Failure<AttendanceReportDto>(notFound);
        }

        var (from, to) = (range.Value.From, range.Value.To);
        var facts = await cache.GetOrCreateAsync(
            TenantCacheKey.For(tenant, CacheArea.Reports, "attendance", from.FirstDay, to.LastDay, today),
            async ct => new AttendanceFacts(
                await groups.ListAsync(ct),
                await readModel.CountAttendanceAsync(from.FirstDay, to.LastDay, ct)),
            cancellationToken);

        return Result.Success(Build(facts, range.Value, ReportInput.Dimension(query.By), query.SportId, query.CoachId));
    }

    internal static AttendanceReportDto Build(AttendanceFacts facts, ReportRange range, ReportDimension by, Guid? sportId, Guid? coachId)
    {
        var periods = range.Periods().Select(p => p.ToString()).ToList();
        var groupsById = facts.Groups.Where(g => ReportRows.Matches(g, sportId, coachId)).ToDictionary(g => g.Id);
        var counts = facts.Counts.Where(c => groupsById.ContainsKey(c.GroupId)).ToList();

        var rows = counts
            .GroupBy(c => ReportRows.KeyOf(groupsById[c.GroupId], by))
            .Select(row => Row(row.Key.Id, row.Key.Name, [.. row], periods));
        var ordered = ReportRows.Ordered(rows, r => r.Id, r => r.Name).ToList();

        return new AttendanceReportDto(
            range.From.ToString(),
            range.To.ToString(),
            by,
            sportId,
            coachId,
            periods,
            ordered,
            Row(null, string.Empty, counts, periods));
    }

    private static AttendanceRowDto Row(Guid? id, string name, List<GroupMonthAttendance> counts, List<string> periods) =>
        new(
            id,
            name,
            [.. periods.Select(period => Cell(period, counts.Where(c => c.Period == period)))],
            Cell(null, counts));

    private static AttendanceCellDto Cell(string? period, IEnumerable<GroupMonthAttendance> counts)
    {
        var list = counts.ToList();
        var (present, late, absent, excused) = (list.Sum(c => c.Present), list.Sum(c => c.Late), list.Sum(c => c.Absent), list.Sum(c => c.Excused));
        return new AttendanceCellDto(period, present, late, absent, excused, ReportRows.Ratio(present + late, present + late + absent));
    }
}

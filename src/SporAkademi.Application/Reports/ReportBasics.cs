using System.Globalization;
using FluentValidation;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Reports;

/// <summary>How a report's rows are grouped (P3-09, DEC-406): per group, per sport (branş) or per the group's coach.</summary>
public enum ReportDimension
{
    Group,
    Sport,
    Coach,
}

/// <summary>Errors of the reports.</summary>
public static class ReportErrors
{
    public static readonly Error InvalidRange = Error.Validation(
        "Report.InvalidRange",
        "The month range must be at most 12 months, within the last 24 months and not after the current month.");
}

/// <summary>A report's month range, resolved against the school's current month (DEC-406).</summary>
public sealed record ReportRange(YearMonth From, YearMonth To)
{
    /// <summary>The longest range one report covers.</summary>
    public const int MaxMonths = 12;

    /// <summary>The range when none is given: the current month and the five before it, like the dashboard's series.</summary>
    public const int DefaultMonths = 6;

    /// <summary>How far back a report may start: the dues window (DEC-183), also the default retention of personal data.</summary>
    public const int HorizonMonths = 24;

    public int Months => MonthsBetween(From, To);

    public IEnumerable<YearMonth> Periods()
    {
        for (var period = From; period <= To; period = period.Next())
        {
            yield return period;
        }
    }

    /// <summary>
    /// <paramref name="to"/> defaults to the current month, <paramref name="from"/> to <see cref="DefaultMonths"/> months
    /// up to <c>to</c> (not before the horizon). The texts are already validated as <c>YYYY-MM</c>.
    /// </summary>
    public static Result<ReportRange> Resolve(string? from, string? to, YearMonth currentPeriod)
    {
        ArgumentNullException.ThrowIfNull(currentPeriod);
        var horizon = Shift(currentPeriod, 1 - HorizonMonths);
        var end = to is null ? currentPeriod : YearMonth.Parse(to).Value;
        var start = from is null ? Max(Shift(end, 1 - DefaultMonths), horizon) : YearMonth.Parse(from).Value;

        return end > currentPeriod || start > end || start < horizon || MonthsBetween(start, end) > MaxMonths
            ? Result.Failure<ReportRange>(ReportErrors.InvalidRange)
            : Result.Success(new ReportRange(start, end));
    }

    /// <summary>The day a month's head counts are taken: its last day, or today for the current month (DEC-407).</summary>
    public static DateOnly ReferenceDate(YearMonth period, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(period);
        return period == YearMonth.From(today) ? today : period.LastDay;
    }

    private static int MonthsBetween(YearMonth from, YearMonth to) => ((to.Year - from.Year) * 12) + to.Month - from.Month + 1;

    private static YearMonth Shift(YearMonth period, int months) => YearMonth.From(period.FirstDay.AddMonths(months));

    private static YearMonth Max(YearMonth a, YearMonth b) => a >= b ? a : b;
}

internal static class ReportInput
{
    public static readonly string[] Dimensions = ["group", "sport", "coach"];

    public static bool IsPeriod(string? text) => text is null || YearMonth.Parse(text).IsSuccess;

    public static bool IsDimension(string? text) => text is null || Dimensions.Contains(text, StringComparer.OrdinalIgnoreCase);

    public static ReportDimension Dimension(string? text) => text?.ToUpperInvariant() switch
    {
        null or "GROUP" => ReportDimension.Group,
        "SPORT" => ReportDimension.Sport,
        "COACH" => ReportDimension.Coach,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown report dimension."),
    };

    public static void AddRangeRules<T>(AbstractValidator<T> validator, Func<T, string?> from, Func<T, string?> to)
    {
        validator.RuleFor(q => from(q)).Must(IsPeriod).WithErrorCode(YearMonth.Invalid.Code).OverridePropertyName("from");
        validator.RuleFor(q => to(q)).Must(IsPeriod).WithErrorCode(YearMonth.Invalid.Code).OverridePropertyName("to");
    }
}

/// <summary>
/// A group as the reports see it (any status), with its sport's and coach's names. Plain data, so it can be cached
/// (DEC-410). The coach is the group's current coach: coach history is not kept (DEC-406).
/// </summary>
public sealed record ReportGroup(
    Guid Id,
    string Name,
    Guid SportId,
    string SportName,
    Guid? CoachId,
    string? CoachName,
    int Capacity,
    bool IsArchived,
    DateOnly? ArchivedOn,
    DateOnly? FirstEnrollmentStart);

/// <summary>A group of the school with the date its first enrollment started, read for the reports.</summary>
public sealed record ReportGroupRow(
    Guid Id,
    string Name,
    Guid SportId,
    Guid? CoachId,
    int Capacity,
    GroupStatus Status,
    DateOnly? ArchivedOn,
    DateOnly? FirstEnrollmentStart);

/// <summary>Attendance record counts of one group in one month.</summary>
public sealed record GroupMonthAttendance(Guid GroupId, string Period, int Present, int Late, int Absent, int Excused);

/// <summary>An enrollment's span in a group (no student: the occupancy report only counts, DEC-409).</summary>
public sealed record EnrollmentSpan(Guid GroupId, DateOnly StartDate, DateOnly? EndDate)
{
    /// <summary>Active on <paramref name="date"/>; the end date is the last active day, like <c>DueCalculator</c>.</summary>
    public bool IsActiveOn(DateOnly date) => StartDate <= date && (EndDate is not { } end || end >= date);
}

/// <summary>
/// Read model of the reports (P3-09): aggregated in the database across the Groups, Enrollments and Attendance tables,
/// read-only, tenant filter applies. A fixed number of queries per report, independent of the school's size.
/// </summary>
public interface IReportsReadModel
{
    /// <summary>Every group of the school (any status) with the start date of its earliest enrollment.</summary>
    Task<IReadOnlyList<ReportGroupRow>> ListGroupsAsync(CancellationToken cancellationToken);

    /// <summary>Attendance record counts per group and session month between the two dates (inclusive); empty pairs are omitted.</summary>
    Task<IReadOnlyList<GroupMonthAttendance>> CountAttendanceAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Enrollments (any status) active at least one day between the two dates (inclusive).</summary>
    Task<IReadOnlyList<EnrollmentSpan>> ListEnrollmentSpansAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken);
}

/// <summary>The school's groups with their sport and coach names: three queries, whatever the number of groups.</summary>
internal sealed class ReportGroups(IReportsReadModel readModel, IOrganizationDirectory organization, ICoachDirectory coaches)
{
    public async Task<IReadOnlyList<ReportGroup>> ListAsync(CancellationToken cancellationToken)
    {
        var groups = await readModel.ListGroupsAsync(cancellationToken);
        var sports = await organization.GetSportsAsync([.. groups.Select(g => g.SportId).Distinct()], cancellationToken);
        var coachIds = groups.Where(g => g.CoachId is not null).Select(g => g.CoachId!.Value).Distinct().ToList();
        var coachNames = coachIds.Count == 0 ? new Dictionary<Guid, CoachInfo>() : await coaches.GetManyAsync(coachIds, cancellationToken);

        return
        [
            .. groups.Select(g => new ReportGroup(
                g.Id,
                g.Name,
                g.SportId,
                sports.GetValueOrDefault(g.SportId)?.Name ?? string.Empty,
                g.CoachId,
                g.CoachId is { } coach && coachNames.TryGetValue(coach, out var info) ? $"{info.FirstName} {info.LastName}" : null,
                g.Capacity,
                g.Status == GroupStatus.Archived,
                g.ArchivedOn,
                g.FirstEnrollmentStart)),
        ];
    }
}

/// <summary>
/// The drill-down filters name a sport or coach of the caller's own school; another school's (or an unknown) id answers
/// <c>404</c> like every other cross-school reference (ADR-0003), rather than an empty report.
/// </summary>
internal sealed class ReportFilters(IOrganizationDirectory organization, ICoachDirectory coaches)
{
    public async Task<Error?> CheckAsync(Guid? sportId, Guid? coachId, CancellationToken cancellationToken)
    {
        if (sportId is { } sport && await organization.FindSportAsync(Domain.Organization.SportId.From(sport), cancellationToken) is null)
        {
            return Domain.Organization.SportErrors.NotFound;
        }

        return coachId is { } coach && await coaches.FindAsync(coach, cancellationToken) is null
            ? Domain.Identity.UserErrors.NotFound
            : null;
    }
}

/// <summary>Grouping and ordering shared by the reports.</summary>
internal static class ReportRows
{
    public static readonly StringComparer Turkish = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    /// <summary>The row a group belongs to under <paramref name="by"/>: its id and name (no coach: null id, empty name).</summary>
    public static (Guid? Id, string Name) KeyOf(ReportGroup group, ReportDimension by) => by switch
    {
        ReportDimension.Group => (group.Id, group.Name),
        ReportDimension.Sport => (group.SportId, group.SportName),
        ReportDimension.Coach => (group.CoachId, group.CoachId is null ? string.Empty : group.CoachName ?? string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(by), by, "Unknown report dimension."),
    };

    /// <summary>The groups a drill-down keeps: of one sport and/or one coach.</summary>
    public static bool Matches(ReportGroup group, Guid? sportId, Guid? coachId) =>
        (sportId is null || group.SportId == sportId) && (coachId is null || group.CoachId == coachId);

    /// <summary>Rows by name (Turkish order); the "no coach" row last.</summary>
    public static IOrderedEnumerable<T> Ordered<T>(IEnumerable<T> rows, Func<T, Guid?> id, Func<T, string> name) =>
        rows.OrderBy(r => id(r) is null).ThenBy(name, Turkish).ThenBy(r => id(r));

    /// <summary>A ratio with 4 decimals, null when there is nothing to divide by (like the dashboard, DEC-174).</summary>
    public static decimal? Ratio(decimal part, decimal whole) =>
        whole == 0 ? null : Math.Round(part / whole, 4, MidpointRounding.AwayFromZero);
}

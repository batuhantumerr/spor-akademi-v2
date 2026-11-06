using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Attendance.Contracts;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Dashboard;

/// <summary>
/// The headline numbers. Ratios are 0–1 with 4 decimals (like the attendance rate, DEC-174) and <c>null</c> when
/// there is nothing to divide by.
/// </summary>
public sealed record DashboardKpisDto(
    int ActiveStudents,
    int ActiveGroups,
    int TotalCapacity,
    int ActiveEnrollments,
    decimal? CapacityUtilisation,
    MoneyDto CollectedThisMonth,
    MoneyDto OutstandingThisMonth,
    MoneyDto OutstandingTotal,
    decimal? AttendanceRateLast30Days);

/// <summary>Collected by payment date, due by fee period.</summary>
public sealed record MonthlyBillingDto(string Period, MoneyDto Collected, MoneyDto Due);

/// <summary>A Monday–Sunday week; the current week counts up to today.</summary>
public sealed record WeeklyAttendanceDto(DateOnly WeekStart, DateOnly WeekEnd, int Counted, decimal? Rate);

public sealed record SportEnrollmentsDto(Guid SportId, string SportName, int ActiveEnrollments);

public sealed record GroupUtilisationDto(Guid GroupId, string GroupName, int ActiveEnrollments, int Capacity, decimal Utilisation);

/// <summary>Today's planned sessions of active groups and how many of them already have attendance.</summary>
public sealed record TodaySessionsDto(int Planned, int Recorded);

/// <summary>Everything the School Admin's home page shows, in one response (DEC-210). Dates are in the school's time zone.</summary>
public sealed record DashboardDto(
    DateOnly Today,
    string CurrentPeriod,
    DashboardKpisDto Kpis,
    IReadOnlyList<MonthlyBillingDto> MonthlyBilling,
    IReadOnlyList<WeeklyAttendanceDto> WeeklyAttendance,
    IReadOnlyList<SportEnrollmentsDto> EnrollmentsBySport,
    IReadOnlyList<GroupUtilisationDto> GroupUtilisation,
    TodaySessionsDto TodaySessions);

/// <summary>An active group with its capacity, active enrollment count and weekly slots.</summary>
public sealed record GroupLoad(GroupId Id, string Name, Guid SportId, int Capacity, int ActiveEnrollments, IReadOnlyList<ScheduleSlot> Slots);

/// <summary>
/// Read model for the dashboard's plain counts (DEC-210): aggregated in the database across the Students, Groups and
/// Enrollments tables, read-only, tenant filter applies. Business rules (dues, rates) are not computed here.
/// </summary>
public interface IDashboardReadModel
{
    Task<int> CountActiveStudentsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GroupLoad>> ListActiveGroupLoadsAsync(CancellationToken cancellationToken);
}

public sealed record GetDashboardQuery : IQuery<DashboardDto>
{
    public const int BillingMonths = 6;
    public const int AttendanceWeeks = 8;
    public const int AttendanceRateDays = 30;
    public const int TopGroups = 10;
}

internal sealed class GetDashboardHandler(
    IDashboardReadModel readModel,
    IBillingSummary billing,
    IAttendanceSummary attendance,
    IOrganizationDirectory organization,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery query, CancellationToken cancellationToken)
    {
        var today = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        var currentPeriod = YearMonth.From(today);

        // A fixed number of queries, independent of the school's size: no per-group or per-student round trips.
        var activeStudents = await readModel.CountActiveStudentsAsync(cancellationToken);
        var groups = await readModel.ListActiveGroupLoadsAsync(cancellationToken);
        var sports = await organization.GetSportsAsync([.. groups.Select(g => g.SportId).Distinct()], cancellationToken);
        var money = await billing.SummarizeAsync(currentPeriod, GetDashboardQuery.BillingMonths, cancellationToken);

        var thisWeek = StartOfWeek(today);
        var firstWeek = thisWeek.AddDays(-7 * (GetDashboardQuery.AttendanceWeeks - 1));
        var rateFrom = today.AddDays(1 - GetDashboardQuery.AttendanceRateDays);
        var firstDay = firstWeek < rateFrom ? firstWeek : rateFrom;
        var days = await attendance.CountByDayAsync(firstDay, today, cancellationToken);
        var recorded = await attendance.RecordedSessionsOnAsync(today, cancellationToken);

        var capacity = groups.Sum(g => g.Capacity);
        var enrolled = groups.Sum(g => g.ActiveEnrollments);
        var kpis = new DashboardKpisDto(
            activeStudents,
            groups.Count,
            capacity,
            enrolled,
            Ratio(enrolled, capacity),
            Money(money.CollectedThisMonth),
            Money(money.OutstandingThisMonth),
            Money(money.OutstandingTotal),
            Rate(days.Where(d => d.Date >= rateFrom)));

        var weekly = Enumerable.Range(0, GetDashboardQuery.AttendanceWeeks)
            .Select(i => firstWeek.AddDays(7 * i))
            .Select(start =>
            {
                var end = start.AddDays(6);
                var inWeek = days.Where(d => d.Date >= start && d.Date <= end).ToList();
                return new WeeklyAttendanceDto(start, end, inWeek.Sum(Counted), Rate(inWeek));
            })
            .ToList();

        var bySport = groups
            .GroupBy(g => g.SportId)
            .Select(g => new SportEnrollmentsDto(g.Key, sports.GetValueOrDefault(g.Key)?.Name ?? string.Empty, g.Sum(x => x.ActiveEnrollments)))
            .OrderByDescending(s => s.ActiveEnrollments)
            .ThenBy(s => s.SportName, Turkish)
            .ToList();

        var utilisation = groups
            .Where(g => g.Capacity > 0)
            .Select(g => new GroupUtilisationDto(g.Id.Value, g.Name, g.ActiveEnrollments, g.Capacity, Ratio(g.ActiveEnrollments, g.Capacity)!.Value))
            .OrderByDescending(g => g.Utilisation)
            .ThenByDescending(g => g.ActiveEnrollments)
            .ThenBy(g => g.GroupName, Turkish)
            .Take(GetDashboardQuery.TopGroups)
            .ToList();

        // Today's sessions come from today's schedule (like /me/sessions); one counts as recorded when a stored
        // session of that group and start time has attendance.
        var recordedKeys = recorded.Select(r => (r.GroupId, r.StartTime)).ToHashSet();
        var planned = groups.SelectMany(g => g.Slots.Where(s => s.Day == today.DayOfWeek).Select(s => (g.Id, s.Time.Start))).ToList();
        var todaySessions = new TodaySessionsDto(planned.Count, planned.Count(recordedKeys.Contains));

        return Result.Success(new DashboardDto(
            today,
            currentPeriod.ToString(),
            kpis,
            [.. money.Months.Select(m => new MonthlyBillingDto(m.Period.ToString(), Money(m.Collected), Money(m.Due)))],
            weekly,
            bySport,
            utilisation,
            todaySessions));
    }

    private static readonly StringComparer Turkish = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    private static DateOnly StartOfWeek(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static int Counted(DailyAttendance day) => day.Present + day.Late + day.Absent;

    /// <summary>DEC-174: (present + late) / (present + late + absent); excused and unrecorded do not count.</summary>
    private static decimal? Rate(IEnumerable<DailyAttendance> days)
    {
        var list = days.ToList();
        return Ratio(list.Sum(d => d.Present + d.Late), list.Sum(Counted));
    }

    private static decimal? Ratio(int part, int whole) =>
        whole == 0 ? null : Math.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);

    private static MoneyDto Money(decimal amount) => MoneyDto.From(Domain.SharedKernel.Money.Try(amount));
}

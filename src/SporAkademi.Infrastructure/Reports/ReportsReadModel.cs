using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Reports;
using SporAkademi.Domain.Attendance;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Reports;

/// <summary>
/// The reports' queries (P3-09, DEC-410). Reads several modules' tables with aggregation in the database; never writes
/// and holds no business rule. The tenant query filters apply to every table read here, and every query starts from the
/// school's rows through an index led by <c>tenant_id</c>.
/// </summary>
internal sealed class ReportsReadModel(AppDbContext db) : IReportsReadModel
{
    public async Task<IReadOnlyList<ReportGroupRow>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        // Two queries: the groups (only the columns needed, not the jsonb schedule) and each group's earliest enrollment.
        var groups = await db.Set<Group>().AsNoTracking()
            .Select(g => new { g.Id, g.Name, g.SportId, g.CoachId, g.Capacity, g.Status, g.ArchivedOn })
            .ToListAsync(cancellationToken);
        var firstStarts = await db.Set<Enrollment>().AsNoTracking()
            .GroupBy(e => e.GroupId)
            .Select(g => new { GroupId = g.Key, First = g.Min(e => e.StartDate) })
            .ToDictionaryAsync(g => g.GroupId, g => g.First, cancellationToken);

        return
        [
            .. groups.Select(g => new ReportGroupRow(
                g.Id.Value,
                g.Name,
                g.SportId.Value,
                g.CoachId?.Value,
                g.Capacity,
                g.Status,
                g.ArchivedOn,
                firstStarts.TryGetValue(g.Id, out var first) ? first : null)),
        ];
    }

    public async Task<IReadOnlyList<GroupMonthAttendance>> CountAttendanceAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        // One grouped query: records per group, session month and status (at most groups × months × 4 rows).
        var rows = await (
                from session in db.Set<Session>().AsNoTracking()
                where session.Date >= firstDay && session.Date <= lastDay
                from record in session.Records
                group record by new { session.GroupId, session.Date.Year, session.Date.Month, record.Status } into g
                select new { g.Key.GroupId, g.Key.Year, g.Key.Month, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.GroupBy(r => (r.GroupId, r.Year, r.Month))
                .Select(g => new GroupMonthAttendance(
                    g.Key.GroupId.Value,
                    YearMonth.Create(g.Key.Year, g.Key.Month).Value.ToString(),
                    g.Where(r => r.Status == AttendanceStatus.Present).Sum(r => r.Count),
                    g.Where(r => r.Status == AttendanceStatus.Late).Sum(r => r.Count),
                    g.Where(r => r.Status == AttendanceStatus.Absent).Sum(r => r.Count),
                    g.Where(r => r.Status == AttendanceStatus.Excused).Sum(r => r.Count))),
        ];
    }

    public async Task<IReadOnlyList<EnrollmentSpan>> ListEnrollmentSpansAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        // Three columns per enrollment; no student is read (DEC-409).
        var spans = await db.Set<Enrollment>().AsNoTracking()
            .Where(e => e.StartDate <= lastDay && (e.EndDate == null || e.EndDate >= firstDay))
            .Select(e => new { e.GroupId, e.StartDate, e.EndDate })
            .ToListAsync(cancellationToken);

        return [.. spans.Select(s => new EnrollmentSpan(s.GroupId.Value, s.StartDate, s.EndDate))];
    }
}

using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Attendance.Contracts;
using SporAkademi.Domain.Attendance;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Attendance;

/// <summary>Aggregated attendance counts for the dashboard; grouped in the database (tenant filter applies).</summary>
internal sealed class AttendanceSummary(AppDbContext db) : IAttendanceSummary
{
    public async Task<IReadOnlyList<DailyAttendance>> CountByDayAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
    {
        var rows = await (
                from session in db.Set<Session>().AsNoTracking()
                where session.Date >= firstDay && session.Date <= lastDay
                from record in session.Records
                group record by new { session.Date, record.Status } into g
                select new StatusCount(g.Key.Date, g.Key.Status, g.Count()))
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.GroupBy(r => r.Date)
                .OrderBy(g => g.Key)
                .Select(g => new DailyAttendance(
                    g.Key,
                    Count(g, AttendanceStatus.Present),
                    Count(g, AttendanceStatus.Late),
                    Count(g, AttendanceStatus.Absent),
                    Count(g, AttendanceStatus.Excused))),
        ];
    }

    public async Task<IReadOnlyList<RecordedSession>> RecordedSessionsOnAsync(DateOnly sessionDate, CancellationToken cancellationToken) =>
        await db.Set<Session>().AsNoTracking()
            .Where(s => s.Date == sessionDate && s.Records.Any())
            .Select(s => new RecordedSession(s.GroupId, s.StartTime))
            .ToListAsync(cancellationToken);

    private static int Count(IEnumerable<StatusCount> rows, AttendanceStatus status) => rows.Where(r => r.Status == status).Sum(r => r.Count);

    private sealed record StatusCount(DateOnly Date, AttendanceStatus Status, int Count);
}

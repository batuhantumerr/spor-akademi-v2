using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Dashboard;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Dashboard;

/// <summary>
/// The dashboard's count queries (DEC-210). Reads several modules' tables in a fixed number of aggregated queries;
/// it never writes and holds no business rule. The tenant query filters apply to every table read here.
/// </summary>
internal sealed class DashboardReadModel(AppDbContext db) : IDashboardReadModel
{
    public Task<int> CountActiveStudentsAsync(CancellationToken cancellationToken) =>
        db.Set<Student>().CountAsync(s => s.Status == StudentStatus.Active, cancellationToken);

    public async Task<IReadOnlyList<GroupLoad>> ListActiveGroupLoadsAsync(CancellationToken cancellationToken)
    {
        // Two queries: the active groups (with their jsonb schedule) and the active enrollment count per group.
        var groups = await db.Set<Group>().AsNoTracking()
            .Where(g => g.Status == GroupStatus.Active)
            .ToListAsync(cancellationToken);
        var counts = await db.Set<Enrollment>().AsNoTracking()
            .Where(e => e.Status == EnrollmentStatus.Active)
            .GroupBy(e => e.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.GroupId, g => g.Count, cancellationToken);

        return
        [
            .. groups.Select(g => new GroupLoad(
                g.Id, g.Name, g.SportId.Value, g.Capacity, counts.GetValueOrDefault(g.Id), g.ScheduleSlots)),
        ];
    }
}

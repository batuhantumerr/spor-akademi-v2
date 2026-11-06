using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Groups;

/// <summary>Groups of the current school for other modules (tenant filter applies).</summary>
internal sealed class GroupDirectory(AppDbContext db) : IGroupDirectory
{
    public async Task<GroupInfo?> FindAsync(GroupId groupId, CancellationToken cancellationToken) =>
        (await GetManyAsync([groupId.Value], cancellationToken)).GetValueOrDefault(groupId.Value);

    public async Task<IReadOnlyDictionary<Guid, GroupInfo>> GetManyAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Count == 0)
        {
            return new Dictionary<Guid, GroupInfo>();
        }

        var ids = groupIds.Distinct().Select(GroupId.From).ToList();
        var groups = await db.Set<Group>().AsNoTracking().Where(g => ids.Contains(g.Id)).ToListAsync(cancellationToken);
        return groups.ToDictionary(
            g => g.Id.Value,
            g => new GroupInfo(g.Id.Value, g.Name, g.Status, g.BirthYearRange, g.Capacity, g.CoachId?.Value, g.ScheduleSlots, g.ArchivedOn));
    }

    public async Task<IReadOnlyList<GroupId>> ListIdsOfSportAsync(SportId sportId, CancellationToken cancellationToken) =>
        await db.Set<Group>().Where(g => g.SportId == sportId).Select(g => g.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GroupId>> ListActiveIdsOfCoachAsync(Guid coachId, CancellationToken cancellationToken)
    {
        var coach = UserId.From(coachId);
        return await db.Set<Group>()
            .Where(g => g.CoachId == coach && g.Status == GroupStatus.Active)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);
    }
}

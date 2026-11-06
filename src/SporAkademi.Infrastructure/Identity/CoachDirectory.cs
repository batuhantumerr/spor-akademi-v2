using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// Coaches of the caller's school. Users are not tenant-filtered (DEC-105), so the school condition is
/// written here explicitly; without a school nothing is found.
/// </summary>
internal sealed class CoachDirectory(AppDbContext db, ICurrentUser currentUser) : ICoachDirectory
{
    public async Task<CoachInfo?> FindAsync(Guid coachId, CancellationToken cancellationToken) =>
        (await GetManyAsync([coachId], cancellationToken)).GetValueOrDefault(coachId);

    public async Task<IReadOnlyDictionary<Guid, CoachInfo>> GetManyAsync(IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coachIds);
        if (currentUser.TenantId is not { } tenant || coachIds.Count == 0)
        {
            return new Dictionary<Guid, CoachInfo>();
        }

        var ids = coachIds.Select(UserId.From).ToList();
        var coaches = await db.Set<User>().AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.Role == UserRole.Coach && u.TenantId == Domain.Tenancy.SchoolId.From(tenant))
            .Select(u => new CoachInfo(u.Id.Value, u.FirstName, u.LastName, u.Status == UserStatus.Active))
            .ToListAsync(cancellationToken);
        return coaches.ToDictionary(c => c.Id);
    }
}

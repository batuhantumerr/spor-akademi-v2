using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>
/// Coaches of the caller's school for other modules. Users are not tenant-filtered (DEC-105), so the school
/// and the Coach role are stated explicitly; without a school in the token nothing is found (deny by default).
/// </summary>
internal sealed class CoachDirectory(AppDbContext dbContext, ICurrentUser currentUser) : ICoachDirectory
{
    public async Task<CoachInfo?> FindAsync(Guid coachId, CancellationToken cancellationToken)
    {
        var found = await GetManyAsync([coachId], cancellationToken);
        return found.GetValueOrDefault(coachId);
    }

    public async Task<IReadOnlyDictionary<Guid, CoachInfo>> GetManyAsync(IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coachIds);
        if (currentUser.TenantId is not { } tenant || coachIds.Count == 0)
        {
            return new Dictionary<Guid, CoachInfo>();
        }

        var school = SchoolId.From(tenant);
        var ids = coachIds.Distinct().Select(UserId.From).ToList();
        var coaches = await dbContext.Set<User>().AsNoTracking()
            .Where(u => u.TenantId == school && u.Role == UserRole.Coach && ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Status })
            .ToListAsync(cancellationToken);

        return coaches.ToDictionary(
            c => c.Id.Value,
            c => new CoachInfo(c.Id.Value, c.FirstName, c.LastName, c.Status == UserStatus.Active));
    }
}

using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

/// <summary>Users are not tenant-filtered (DEC-105): every school-bound query states its school explicitly.</summary>
internal sealed class UserRepository(AppDbContext dbContext) : IUserRepository
{
    private IQueryable<User> UsersWithTokens => dbContext.Set<User>().Include(u => u.RefreshTokens).Include(u => u.PasswordTokens);

    public Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<User?> GetByRefreshTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.TokenHash == tokenHash), cancellationToken);

    public Task<User?> GetByPasswordTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.PasswordTokens.Any(t => t.TokenHash == tokenHash), cancellationToken);

    public async Task<UserSummary?> GetSummaryAsync(UserId id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : UserSummary.From(user);
    }

    public Task<bool> EmailExistsAsync(Email email, CancellationToken cancellationToken) =>
        dbContext.Set<User>().AnyAsync(u => u.Email == email, cancellationToken);

    public void Add(User user) => dbContext.Add(user);

    public void ExpectVersion(User user, uint version)
    {
        var entry = dbContext.Entry(user);
        entry.Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;
        // DEC-134: always issue the version-checked UPDATE, so a no-op edit with a stale version still conflicts.
        entry.State = EntityState.Modified;
    }

    public Task<User?> GetCoachAsync(SchoolId schoolId, UserId id, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == schoolId && u.Role == UserRole.Coach, cancellationToken);

    public Task<CoachDto?> FindCoachDtoAsync(SchoolId schoolId, UserId id, CancellationToken cancellationToken) =>
        ProjectCoaches(Coaches(schoolId).Where(u => u.Id == id)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<CoachDto>> ListCoachesAsync(SchoolId schoolId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = Coaches(schoolId);
        var total = await query.CountAsync(cancellationToken);
        var items = await ProjectCoaches(query.OrderBy(u => u.LastName).ThenBy(u => u.FirstName).ThenBy(u => u.Id).Skip(page.Skip).Take(page.PageSize))
            .ToListAsync(cancellationToken);
        return new PagedResult<CoachDto>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyDictionary<SchoolId, SchoolAdminSummary>> GetFirstSchoolAdminsAsync(
        IReadOnlyCollection<SchoolId> schoolIds,
        CancellationToken cancellationToken)
    {
        var ids = schoolIds.Select(s => (SchoolId?)s).ToList();
        var admins = await dbContext.Set<User>().AsNoTracking()
            .Where(u => u.Role == UserRole.SchoolAdmin && ids.Contains(u.TenantId))
            .ToListAsync(cancellationToken);

        // UUID v7 ids grow with creation time, so the smallest id is the first admin.
        return admins
            .GroupBy(u => u.TenantId!.Value)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var first = g.MinBy(u => u.Id.Value)!;
                    return new SchoolAdminSummary(first.Id.Value, first.FirstName, first.LastName, first.Email.Value, first.Status);
                });
    }

    public async Task<IReadOnlySet<Guid>> GetActiveCoachIdsAsync(SchoolId schoolId, IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken)
    {
        var ids = coachIds.Select(UserId.From).ToList();
        var active = await Coaches(schoolId)
            .Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return active.Select(id => id.Value).ToHashSet();
    }

    public Task<User?> GetParentAsync(SchoolId schoolId, UserId id, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Id == id && u.TenantId == schoolId && u.Role == UserRole.Parent, cancellationToken);

    public async Task<IReadOnlyList<User>> ListParentsAsync(SchoolId schoolId, IReadOnlyCollection<UserId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.ToList();
        return await dbContext.Set<User>().AsNoTracking().Include(u => u.PasswordTokens)
            .Where(u => u.TenantId == schoolId && u.Role == UserRole.Parent && list.Contains(u.Id))
            .ToListAsync(cancellationToken);
    }

    private IQueryable<User> Coaches(SchoolId schoolId) =>
        dbContext.Set<User>().AsNoTracking().Where(u => u.TenantId == schoolId && u.Role == UserRole.Coach);

    private static IQueryable<CoachDto> ProjectCoaches(IQueryable<User> query) =>
        query.Select(u => new CoachDto(
            u.Id.Value,
            u.FirstName,
            u.LastName,
            u.Email.Value,
            u.Phone == null ? null : u.Phone.Value,
            u.Status,
            EF.Property<uint>(u, ConcurrencyToken.PropertyName)));
}

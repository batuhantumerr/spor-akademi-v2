using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Identity;

internal sealed class UserRepository(AppDbContext dbContext) : IUserRepository
{
    private IQueryable<User> UsersWithTokens => dbContext.Set<User>().Include(u => u.RefreshTokens);

    public Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<User?> GetByRefreshTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        UsersWithTokens.SingleOrDefaultAsync(u => u.RefreshTokens.Any(t => t.TokenHash == tokenHash), cancellationToken);

    public async Task<UserSummary?> GetSummaryAsync(UserId id, CancellationToken cancellationToken)
    {
        var user = await dbContext.Set<User>().AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? null : UserSummary.From(user);
    }
}

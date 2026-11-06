using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class TenancyModule(AppDbContext dbContext, TimeProvider timeProvider) : ITenancyModule
{
    public Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AnyAsync(s => s.Id == schoolId && s.Status == SchoolStatus.Active, cancellationToken);

    public async Task<DateOnly> TodayAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        (await FindAsync(schoolId, cancellationToken)).Today(timeProvider);

    public async Task<DateTime> LocalNowAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        (await FindAsync(schoolId, cancellationToken)).LocalNow(timeProvider);

    private async Task<School> FindAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        await dbContext.Set<School>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == schoolId, cancellationToken)
            ?? throw new InvalidOperationException("The school of a signed-in user must exist.");
}

using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class TenancyModule(AppDbContext dbContext, TimeProvider timeProvider) : ITenancyModule
{
    public Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AnyAsync(s => s.Id == schoolId && s.Status == SchoolStatus.Active, cancellationToken);

    public async Task<DateOnly> GetTodayAsync(SchoolId schoolId, CancellationToken cancellationToken)
    {
        var school = await dbContext.Set<School>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == schoolId, cancellationToken)
            ?? throw new InvalidOperationException("Unknown school.");
        return school.Today(timeProvider);
    }
}

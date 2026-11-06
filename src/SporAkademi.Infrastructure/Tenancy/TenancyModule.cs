using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class TenancyModule(AppDbContext dbContext) : ITenancyModule
{
    public Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AnyAsync(s => s.Id == schoolId && s.Status == SchoolStatus.Active, cancellationToken);
}

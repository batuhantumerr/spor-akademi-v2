using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Tenancy;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class SchoolRepository(AppDbContext dbContext) : ISchoolRepository
{
    public Task<bool> SlugExistsAsync(Slug slug, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AnyAsync(s => s.Slug == slug, cancellationToken);

    public void Add(School school) => dbContext.Add(school);

    public Task<SchoolDto?> FindDtoAsync(SchoolId id, CancellationToken cancellationToken) =>
        Project(dbContext.Set<School>().Where(s => s.Id == id)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PagedResult<SchoolDto>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = dbContext.Set<School>();
        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderBy(s => s.Name).ThenBy(s => s.Id).Skip(page.Skip).Take(page.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<SchoolDto>(items, page.Page, page.PageSize, total);
    }

    private static IQueryable<SchoolDto> Project(IQueryable<School> query) =>
        query.AsNoTracking().Select(s => new SchoolDto(s.Id.Value, s.Name, s.Slug.Value, s.TimeZone, s.Status));
}

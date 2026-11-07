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

    public Task<School?> GetAsync(SchoolId id, CancellationToken cancellationToken) =>
        dbContext.Set<School>().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void ExpectVersion(School school, uint version)
    {
        var entry = dbContext.Entry(school);
        entry.Property<uint>(ConcurrencyToken.PropertyName).OriginalValue = version;
        // Always issue the version-checked UPDATE, so an unchanged value with a stale version still conflicts.
        entry.State = EntityState.Modified;
    }

    public Task<RetentionPolicyDto?> FindRetentionPolicyAsync(SchoolId id, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new RetentionPolicyDto(
                s.RetentionPolicy.PersonalDataMonths,
                s.RetentionPolicy.PaymentYears,
                RetentionPolicy.MinPersonalDataMonths,
                RetentionPolicy.MaxPersonalDataMonths,
                EF.Property<uint>(s, ConcurrencyToken.PropertyName)))
            .SingleOrDefaultAsync(cancellationToken);

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

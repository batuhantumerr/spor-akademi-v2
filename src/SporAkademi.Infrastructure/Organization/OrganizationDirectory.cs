using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Domain.Organization;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Organization;

/// <summary>Sports and facilities of the current school for other modules (tenant filter applies).</summary>
internal sealed class OrganizationDirectory(AppDbContext db) : IOrganizationDirectory
{
    public Task<CatalogItem?> FindSportAsync(SportId sportId, CancellationToken cancellationToken) =>
        db.Set<Sport>().AsNoTracking().Where(s => s.Id == sportId)
            .Select(s => new CatalogItem(s.Id.Value, s.Name, s.IsArchived))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CatalogItem?> FindFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken) =>
        db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId)
            .Select(f => new CatalogItem(f.Id.Value, f.Name, f.IsArchived))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, CatalogItem>> GetSportsAsync(IReadOnlyCollection<Guid> sportIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sportIds);
        if (sportIds.Count == 0)
        {
            return new Dictionary<Guid, CatalogItem>();
        }

        var ids = sportIds.Select(SportId.From).ToList();
        var items = await db.Set<Sport>().AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new CatalogItem(s.Id.Value, s.Name, s.IsArchived))
            .ToListAsync(cancellationToken);
        return items.ToDictionary(i => i.Id);
    }

    public async Task<IReadOnlyDictionary<Guid, CatalogItem>> GetFacilitiesAsync(IReadOnlyCollection<Guid> facilityIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facilityIds);
        if (facilityIds.Count == 0)
        {
            return new Dictionary<Guid, CatalogItem>();
        }

        var ids = facilityIds.Select(FacilityId.From).ToList();
        var items = await db.Set<Facility>().AsNoTracking().Where(f => ids.Contains(f.Id))
            .Select(f => new CatalogItem(f.Id.Value, f.Name, f.IsArchived))
            .ToListAsync(cancellationToken);
        return items.ToDictionary(i => i.Id);
    }
}

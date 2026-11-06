using SporAkademi.Domain.Organization;

namespace SporAkademi.Application.Organization.Contracts;

/// <summary>A sport or facility of the current school as other modules see it.</summary>
public sealed record CatalogItem(Guid Id, string Name, bool IsArchived);

/// <summary>Read contract of the Organization module for sports and facilities of the current school (used by Groups).</summary>
public interface IOrganizationDirectory
{
    Task<CatalogItem?> FindSportAsync(SportId sportId, CancellationToken cancellationToken);

    Task<CatalogItem?> FindFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken);

    /// <summary>Sports of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, CatalogItem>> GetSportsAsync(IReadOnlyCollection<Guid> sportIds, CancellationToken cancellationToken);

    /// <summary>Facilities of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, CatalogItem>> GetFacilitiesAsync(IReadOnlyCollection<Guid> facilityIds, CancellationToken cancellationToken);
}

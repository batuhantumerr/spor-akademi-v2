using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.Organization;

namespace SporAkademi.Application.Organization;

public sealed record SportDto(Guid Id, string Name, bool IsArchived, uint Version);

public sealed record AddressDto(string Line, string District, string City);

public sealed record FacilityDto(
    Guid Id,
    string Name,
    FacilityType Type,
    AddressDto Address,
    int? Capacity,
    bool IsArchived,
    uint Version);

/// <summary>Sports of the current school (tenant filter applies).</summary>
public interface ISportRepository
{
    Task<Sport?> GetAsync(SportId id, CancellationToken cancellationToken);

    Task<bool> NameKeyExistsAsync(string nameKey, SportId? except, CancellationToken cancellationToken);

    void Add(Sport sport);

    void Remove(Sport sport);

    /// <summary>Optimistic concurrency: the save fails with Concurrency.Conflict if the row changed since <paramref name="version"/>.</summary>
    void ExpectVersion(Sport sport, uint version);

    Task<SportDto?> FindDtoAsync(SportId id, CancellationToken cancellationToken);

    Task<PagedResult<SportDto>> ListAsync(bool includeArchived, PageRequest page, CancellationToken cancellationToken);
}

public interface IFacilityRepository
{
    Task<Facility?> GetAsync(FacilityId id, CancellationToken cancellationToken);

    Task<bool> NameKeyExistsAsync(string nameKey, FacilityId? except, CancellationToken cancellationToken);

    void Add(Facility facility);

    void Remove(Facility facility);

    void ExpectVersion(Facility facility, uint version);

    Task<FacilityDto?> FindDtoAsync(FacilityId id, CancellationToken cancellationToken);

    Task<PagedResult<FacilityDto>> ListAsync(bool includeArchived, PageRequest page, CancellationToken cancellationToken);
}

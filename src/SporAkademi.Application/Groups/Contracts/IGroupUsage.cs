using SporAkademi.Domain.Organization;

namespace SporAkademi.Application.Groups.Contracts;

/// <summary>
/// Read contract of the Groups module, used by Organization to decide whether a sport or facility
/// can be deleted (US-06, US-07). Groups arrive in P1-18; until then nothing is in use.
/// </summary>
public interface IGroupUsage
{
    Task<bool> AnyGroupUsesSportAsync(SportId sportId, CancellationToken cancellationToken);

    Task<bool> AnyActiveGroupUsesFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken);
}

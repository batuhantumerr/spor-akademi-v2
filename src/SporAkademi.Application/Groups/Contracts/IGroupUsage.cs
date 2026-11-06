using SporAkademi.Domain.Organization;

namespace SporAkademi.Application.Groups.Contracts;

/// <summary>
/// Read contract of the Groups module, used by Organization to decide whether a sport or facility
/// can be deleted (US-06, US-07). Any group blocks deleting its sport; only active groups are checked
/// for a facility here, archived ones are caught by the foreign key (DEC-146).
/// </summary>
public interface IGroupUsage
{
    Task<bool> AnyGroupUsesSportAsync(SportId sportId, CancellationToken cancellationToken);

    Task<bool> AnyActiveGroupUsesFacilityAsync(FacilityId facilityId, CancellationToken cancellationToken);
}

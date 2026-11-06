using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy.Contracts;

/// <summary>What other modules may ask the Tenancy module (ADR-0009: modules talk through contracts).</summary>
public interface ITenancyModule
{
    /// <summary>False for a deactivated or unknown school.</summary>
    Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken);
}

using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy.Contracts;

/// <summary>What other modules may ask the Tenancy module (ADR-0009: modules talk through contracts).</summary>
public interface ITenancyModule
{
    /// <summary>False for a deactivated or unknown school.</summary>
    Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken);

    /// <summary>Today's date in the school's time zone, from <see cref="TimeProvider"/> (e.g. for the age rules of US-13).</summary>
    Task<DateOnly> GetTodayAsync(SchoolId schoolId, CancellationToken cancellationToken);
}

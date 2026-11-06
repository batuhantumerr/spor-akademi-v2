using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Tenancy.Contracts;

/// <summary>What other modules may ask the Tenancy module (ADR-0009: modules talk through contracts).</summary>
public interface ITenancyModule
{
    /// <summary>False for a deactivated or unknown school.</summary>
    Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken);

    /// <summary>Today's date in the school's time zone ("today" of US-11 and US-12).</summary>
    Task<DateOnly> TodayAsync(SchoolId schoolId, CancellationToken cancellationToken);

    /// <summary>The wall-clock time now in the school's time zone (e.g. whether a session has started, US-19).</summary>
    Task<DateTime> LocalNowAsync(SchoolId schoolId, CancellationToken cancellationToken);

    /// <summary>The school's IANA time zone (e.g. <c>Europe/Istanbul</c>), or null for an unknown school.</summary>
    Task<string?> TimeZoneAsync(SchoolId schoolId, CancellationToken cancellationToken);
}

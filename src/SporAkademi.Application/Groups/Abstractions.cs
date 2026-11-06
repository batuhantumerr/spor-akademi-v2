using System.Globalization;
using SporAkademi.Application.Abstractions;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Groups;

public sealed record BirthYearRangeDto(int From, int To);

/// <summary>A weekly slot; times are <c>HH:mm</c> in the school's time zone (appendix D §1.2).</summary>
public sealed record ScheduleSlotDto(DayOfWeek Day, string Start, string End);

public sealed record NamedReferenceDto(Guid Id, string Name);

/// <summary>The assigned coach; <c>isActive</c> false means "no active coach" in the UI (US-08).</summary>
public sealed record GroupCoachDto(Guid Id, string FirstName, string LastName, bool IsActive);

public sealed record GroupDto(
    Guid Id,
    string Name,
    NamedReferenceDto Sport,
    NamedReferenceDto Facility,
    GroupCoachDto? Coach,
    BirthYearRangeDto BirthYearRange,
    int Capacity,
    GroupStatus Status,
    DateOnly? ArchivedOn,
    IReadOnlyList<ScheduleSlotDto> Slots,
    uint Version);

/// <summary>A planned training (computed from the schedule, appendix B 4.9).</summary>
public sealed record SessionDto(Guid GroupId, string GroupName, DateOnly Date, string Start, string End, NamedReferenceDto Facility);

public sealed record GroupFilter(SportId? SportId, UserId? CoachId, GroupStatus? Status);

/// <summary>A group read without tracking, with the concurrency version of its row.</summary>
public sealed record GroupSnapshot(Group Group, uint Version);

/// <summary>Groups of the current school (tenant filter applies).</summary>
public interface IGroupRepository
{
    /// <summary>Tracked, for changes.</summary>
    Task<Group?> GetAsync(GroupId id, CancellationToken cancellationToken);

    Task<bool> NameKeyExistsAsync(string nameKey, GroupId? except, CancellationToken cancellationToken);

    void Add(Group group);

    /// <summary>Optimistic concurrency: the save fails with Concurrency.Conflict if the row changed since <paramref name="version"/>.</summary>
    void ExpectVersion(Group group, uint version);

    /// <summary>Other active groups that share the coach or the facility: the candidates for schedule conflicts.</summary>
    Task<IReadOnlyList<GroupWeek>> ListActiveWeeksAsync(GroupId except, UserId? coachId, FacilityId facilityId, CancellationToken cancellationToken);

    /// <summary>Tracked groups of the coach in any status, for clearing the coach.</summary>
    Task<IReadOnlyList<Group>> ListByCoachAsync(UserId coachId, CancellationToken cancellationToken);

    Task<GroupSnapshot?> FindAsync(GroupId id, CancellationToken cancellationToken);

    Task<PagedResult<GroupSnapshot>> ListAsync(GroupFilter filter, PageRequest page, CancellationToken cancellationToken);

    Task<IReadOnlyList<GroupSnapshot>> ListActiveOfCoachAsync(UserId coachId, CancellationToken cancellationToken);
}

internal static class TimeFormat
{
    public const string Pattern = "HH:mm";

    public static string Format(TimeOnly time) => time.ToString(Pattern, CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static ScheduleSlotDto ToDto(this ScheduleSlot slot) => new(slot.Day, Format(slot.Time.Start), Format(slot.Time.End));

    public static Result<TimeRange> ToTimeRange(string start, string end) =>
        TryParse(start, out var from) && TryParse(end, out var to)
            ? TimeRange.Create(from, to)
            : Result.Failure<TimeRange>(CommonErrors.FieldInvalid);
}

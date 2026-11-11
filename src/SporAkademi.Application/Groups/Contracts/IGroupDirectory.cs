using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;

namespace SporAkademi.Application.Groups.Contracts;

/// <summary>A group of the current school as other modules see it.</summary>
public sealed record GroupInfo(
    Guid Id,
    string Name,
    GroupStatus Status,
    BirthYearRange BirthYears,
    int Capacity,
    Guid? CoachId,
    IReadOnlyList<ScheduleSlot> Slots,
    DateOnly? ArchivedOn = null,
    Guid? SportId = null,
    Guid? FacilityId = null)
{
    public bool IsArchived => Status == GroupStatus.Archived;

    /// <summary>The planned sessions between two dates, both inclusive (<see cref="SessionCalendar"/>).</summary>
    public IReadOnlyList<PlannedSession> PlannedBetween(DateOnly from, DateOnly to) =>
        SessionCalendar.Between(GroupId.From(Id), Slots, ArchivedOn, from, to);
}

/// <summary>Read contract of the Groups module (used by Enrollments).</summary>
public interface IGroupDirectory
{
    Task<GroupInfo?> FindAsync(GroupId groupId, CancellationToken cancellationToken);

    /// <summary>Groups of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, GroupInfo>> GetManyAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken);

    /// <summary>Ids of every group (any status) of the sport.</summary>
    Task<IReadOnlyList<GroupId>> ListIdsOfSportAsync(SportId sportId, CancellationToken cancellationToken);

    /// <summary>Ids of the active groups the coach currently leads (what a coach may see, US-12).</summary>
    Task<IReadOnlyList<GroupId>> ListActiveIdsOfCoachAsync(Guid coachId, CancellationToken cancellationToken);
}

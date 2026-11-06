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
    IReadOnlyList<ScheduleSlot> Slots)
{
    public bool IsArchived => Status == GroupStatus.Archived;
}

/// <summary>Read contract of the Groups module (used by Enrollments).</summary>
public interface IGroupDirectory
{
    Task<GroupInfo?> FindAsync(GroupId groupId, CancellationToken cancellationToken);

    /// <summary>Groups of the current school among the given ids; unknown ids are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, GroupInfo>> GetManyAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken);

    /// <summary>Ids of every group (any status) of the sport.</summary>
    Task<IReadOnlyList<GroupId>> ListIdsOfSportAsync(SportId sportId, CancellationToken cancellationToken);
}

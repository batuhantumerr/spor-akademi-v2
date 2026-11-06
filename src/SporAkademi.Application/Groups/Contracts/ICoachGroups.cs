using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Application.Groups.Contracts;

/// <summary>Read contract of the Groups module: which groups a coach currently works with (used by Announcements).</summary>
public interface ICoachGroups
{
    /// <summary>The coach's active groups in the current school; archived groups are not included (US-11).</summary>
    Task<IReadOnlyList<GroupId>> ListActiveGroupIdsAsync(UserId coachId, CancellationToken cancellationToken);
}

using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Groups;

/// <summary>What the conflict check needs to know about a group's week.</summary>
public sealed record GroupWeek(GroupId Id, string Name, UserId? CoachId, FacilityId FacilityId, IReadOnlyCollection<ScheduleSlot> Slots)
{
    public static GroupWeek Of(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new GroupWeek(group.Id, group.Name, group.CoachId, group.FacilityId, group.ScheduleSlots);
    }
}

/// <summary>
/// Cross-group schedule rules (US-10, appendix B §5): a coach cannot be in two places at once (error),
/// two groups may share a facility at the same time, which is only a warning (DEC-012: a big field can
/// host two groups).
/// </summary>
public static class ScheduleConflictChecker
{
    public const string FacilityOverlapCode = "Schedule.FacilityOverlap";

    /// <param name="group">The group with its new coach, facility and slots.</param>
    /// <param name="others">The school's other active groups; the group itself is skipped if present.</param>
    /// <returns><c>Schedule.CoachUnavailable</c>, or one <c>Schedule.FacilityOverlap</c> warning per overlapping group.</returns>
    public static Result<IReadOnlyList<Warning>> Check(GroupWeek group, IEnumerable<GroupWeek> others)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(others);

        var warnings = new List<Warning>();
        foreach (var other in others.Where(o => o.Id != group.Id).OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (!Overlap(group.Slots, other.Slots))
            {
                continue;
            }

            if (group.CoachId is { } coach && other.CoachId == coach)
            {
                return Result.Failure<IReadOnlyList<Warning>>(ScheduleErrors.CoachUnavailable(other.Name));
            }

            if (other.FacilityId == group.FacilityId)
            {
                warnings.Add(Warning.Create(FacilityOverlapCode, other.Id.Value).WithArgument("groupName", other.Name));
            }
        }

        return Result.Success<IReadOnlyList<Warning>>(warnings);
    }

    private static bool Overlap(IReadOnlyCollection<ScheduleSlot> left, IReadOnlyCollection<ScheduleSlot> right) =>
        left.Any(l => right.Any(l.Overlaps));
}

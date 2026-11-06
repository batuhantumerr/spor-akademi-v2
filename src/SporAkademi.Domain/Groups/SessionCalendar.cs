using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Groups;

/// <summary>A training a group's schedule plans for a date (computed, never stored; appendix B 4.9).</summary>
public sealed record PlannedSession(GroupId GroupId, DateOnly Date, TimeRange Time);

/// <summary>
/// Expands a group's weekly slots into dated sessions (appendix B §5). Dates and times are local to
/// the school, so no time zone arithmetic happens here.
/// </summary>
public static class SessionCalendar
{
    /// <summary>Sessions from <paramref name="from"/> to <paramref name="to"/>, both inclusive, ordered by date and start.</summary>
    public static IReadOnlyList<PlannedSession> Between(Group group, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Between(group.Id, group.ScheduleSlots, group.ArchivedOn, from, to);
    }

    /// <summary>The same for a group known only by its schedule and archive date (e.g. through a module contract).</summary>
    public static IReadOnlyList<PlannedSession> Between(
        GroupId groupId,
        IReadOnlyCollection<ScheduleSlot> slots,
        DateOnly? archivedOn,
        DateOnly from,
        DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(slots);

        // An archived group trains up to and including the day it was archived.
        var last = archivedOn is { } archived && archived < to ? archived : to;
        var sessions = new List<PlannedSession>();
        // Day numbers instead of AddDays: the loop never steps past DateOnly.MaxValue.
        for (var day = from.DayNumber; day <= last.DayNumber; day++)
        {
            var date = DateOnly.FromDayNumber(day);
            sessions.AddRange(slots
                .Where(slot => slot.Day == date.DayOfWeek)
                .OrderBy(slot => slot.Time.Start)
                .Select(slot => new PlannedSession(groupId, date, slot.Time)));
        }

        return sessions;
    }
}

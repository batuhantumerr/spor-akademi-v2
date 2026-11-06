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

        // An archived group trains up to and including the day it was archived.
        var last = group.ArchivedOn is { } archivedOn && archivedOn < to ? archivedOn : to;
        var sessions = new List<PlannedSession>();
        for (var date = from; date <= last; date = date.AddDays(1))
        {
            sessions.AddRange(group.ScheduleSlots
                .Where(slot => slot.Day == date.DayOfWeek)
                .OrderBy(slot => slot.Time.Start)
                .Select(slot => new PlannedSession(group.Id, date, slot.Time)));
        }

        return sessions;
    }
}

using System.Globalization;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Organization.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Enrollments;

/// <summary>One weekly training slot; times <c>HH:mm</c> in the school's time zone.</summary>
public sealed record ChildScheduleSlotDto(DayOfWeek Day, string Start, string End);

/// <summary>
/// A group the child trains in (P3-03, DEC-384): where, with whom and when. The coach's name only, never contact data.
/// </summary>
public sealed record ChildGroupDto(
    Guid EnrollmentId,
    Guid GroupId,
    string GroupName,
    string SportName,
    string FacilityName,
    string? CoachFirstName,
    string? CoachLastName,
    DateOnly StartDate,
    IReadOnlyList<ChildScheduleSlotDto> Slots);

/// <summary>P3-03: the weekly program of one of the caller's children: the groups of the active enrollments, by group name.</summary>
public sealed record GetChildScheduleQuery(StudentId StudentId) : IQuery<IReadOnlyList<ChildGroupDto>>;

internal sealed class GetChildScheduleHandler(
    IParentChildren children,
    IEnrollmentRepository enrollments,
    IGroupDirectory groups,
    IOrganizationDirectory organization,
    ICoachDirectory coaches) : IQueryHandler<GetChildScheduleQuery, IReadOnlyList<ChildGroupDto>>
{
    private static readonly StringComparer Turkish = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), ignoreCase: true);

    public async Task<Result<IReadOnlyList<ChildGroupDto>>> Handle(GetChildScheduleQuery query, CancellationToken cancellationToken)
    {
        if (!await children.IsMineAsync(query.StudentId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<ChildGroupDto>>(StudentErrors.NotFound);
        }

        var active = (await enrollments.ListActiveOfStudentAsync(query.StudentId, cancellationToken))
            .Where(e => e.Status == EnrollmentStatus.Active)
            .ToList();
        var groupInfos = await groups.GetManyAsync([.. active.Select(e => e.GroupId.Value).Distinct()], cancellationToken);
        var sports = await organization.GetSportsAsync([.. groupInfos.Values.Select(g => g.SportId).OfType<Guid>().Distinct()], cancellationToken);
        var facilities = await organization.GetFacilitiesAsync([.. groupInfos.Values.Select(g => g.FacilityId).OfType<Guid>().Distinct()], cancellationToken);
        var coachInfos = await coaches.GetManyAsync([.. groupInfos.Values.Select(g => g.CoachId).OfType<Guid>().Distinct()], cancellationToken);

        var rows = new List<ChildGroupDto>();
        foreach (var enrollment in active)
        {
            if (!groupInfos.TryGetValue(enrollment.GroupId.Value, out var group) || group.IsArchived)
            {
                continue;
            }

            // A deactivated coach is not named: the group has no active coach (US-08).
            var coach = group.CoachId is { } coachId && coachInfos.TryGetValue(coachId, out var info) && info.IsActive ? info : null;
            rows.Add(new ChildGroupDto(
                enrollment.Id.Value,
                group.Id,
                group.Name,
                group.SportId is { } sport ? sports.GetValueOrDefault(sport)?.Name ?? string.Empty : string.Empty,
                group.FacilityId is { } facility ? facilities.GetValueOrDefault(facility)?.Name ?? string.Empty : string.Empty,
                coach?.FirstName,
                coach?.LastName,
                enrollment.StartDate,
                [.. group.Slots.OrderBy(s => s.Day == DayOfWeek.Sunday ? 7 : (int)s.Day).ThenBy(s => s.Time.Start).Select(Slot)]));
        }

        return Result.Success<IReadOnlyList<ChildGroupDto>>([.. rows.OrderBy(r => r.GroupName, Turkish).ThenBy(r => r.EnrollmentId)]);
    }

    private static ChildScheduleSlotDto Slot(ScheduleSlot slot) =>
        new(slot.Day, slot.Time.Start.ToString("HH:mm", CultureInfo.InvariantCulture), slot.Time.End.ToString("HH:mm", CultureInfo.InvariantCulture));
}

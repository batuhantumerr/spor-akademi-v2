using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Enrollments;

/// <summary>What the policy needs to know about the student.</summary>
public sealed record EnrollmentStudent(StudentId Id, StudentStatus Status, DateOnly BirthDate);

/// <summary>What the policy needs to know about a group: the target, or one the student is already in.</summary>
public sealed record EnrollmentGroup(
    GroupId Id,
    string Name,
    bool IsArchived,
    BirthYearRange BirthYears,
    int Capacity,
    IReadOnlyCollection<ScheduleSlot> Slots);

/// <summary>
/// The cross-aggregate enrollment rules (US-17, appendix B §5). The caller must hold the student and
/// group locks (ADR-0005) while reading the inputs and saving the result, so the counts cannot change.
/// </summary>
public static class EnrollmentPolicy
{
    /// <param name="activeInGroup">Active enrollments of the target group now.</param>
    /// <param name="studentsActiveGroups">Groups the student is actively enrolled in now (may include the target).</param>
    public static Result<Enrollment> Enroll(
        SchoolId tenantId,
        EnrollmentStudent student,
        EnrollmentGroup group,
        int activeInGroup,
        IReadOnlyCollection<EnrollmentGroup> studentsActiveGroups,
        DateOnly startDate)
    {
        ArgumentNullException.ThrowIfNull(student);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(studentsActiveGroups);

        if (student.Status != StudentStatus.Active)
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.StudentNotActive);
        }

        if (group.IsArchived)
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.GroupArchived);
        }

        if (studentsActiveGroups.Any(g => g.Id == group.Id))
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.AlreadyEnrolled);
        }

        if (!group.BirthYears.Contains(student.BirthDate.Year))
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.BirthYearOutOfRange(student.BirthDate.Year, group.BirthYears));
        }

        if (activeInGroup >= group.Capacity)
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.GroupFull);
        }

        var conflict = studentsActiveGroups
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .FirstOrDefault(other => other.Slots.Any(o => group.Slots.Any(o.Overlaps)));
        if (conflict is not null)
        {
            return Result.Failure<Enrollment>(EnrollmentErrors.ScheduleConflict(conflict.Name));
        }

        return Result.Success(Enrollment.Start(tenantId, student.Id, group.Id, startDate));
    }
}

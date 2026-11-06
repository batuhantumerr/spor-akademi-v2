using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Enrollments;

public readonly record struct EnrollmentId(Guid Value) : IEntityId<EnrollmentId>
{
    public static EnrollmentId From(Guid value) => new(value);
}

public enum EnrollmentStatus
{
    Active,
    Ended,
}

/// <summary>
/// A student's membership in a group for a period (appendix B 4.8). A separate aggregate, not part of the
/// group, so a group never loads years of history; the cross-aggregate rules live in <see cref="EnrollmentPolicy"/>.
/// Invariant: <see cref="EndDate"/> is set if and only if the status is Ended, and never before the start.
/// </summary>
public sealed class Enrollment : AggregateRoot<EnrollmentId>, ITenantScoped
{
    private Enrollment(EnrollmentId id, SchoolId tenantId, StudentId studentId, GroupId groupId, DateOnly startDate)
        : base(id)
    {
        TenantId = tenantId;
        StudentId = studentId;
        GroupId = groupId;
        StartDate = startDate;
        Status = EnrollmentStatus.Active;
    }

    // For EF Core materialization only.
    private Enrollment()
        : base(default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public StudentId StudentId { get; private set; }

    public GroupId GroupId { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public EnrollmentStatus Status { get; private set; }

    public bool IsActive => Status == EnrollmentStatus.Active;

    /// <summary>Only <see cref="EnrollmentPolicy"/> creates enrollments, after checking the cross-aggregate rules.</summary>
    internal static Enrollment Start(SchoolId tenantId, StudentId studentId, GroupId groupId, DateOnly startDate) =>
        new(EntityId.New<EnrollmentId>(), tenantId, studentId, groupId, startDate);

    /// <summary>Ends the enrollment on <paramref name="date"/>; the seat is free from that date (US-18). History stays.</summary>
    public Result End(DateOnly date)
    {
        if (!IsActive)
        {
            return Result.Failure(EnrollmentErrors.AlreadyEnded);
        }

        if (date < StartDate)
        {
            return Result.Failure(EnrollmentErrors.InvalidEndDate);
        }

        Status = EnrollmentStatus.Ended;
        EndDate = date;
        return Result.Success();
    }
}

public static class EnrollmentErrors
{
    public static readonly Error NotFound = Error.NotFound("Enrollment.NotFound", "Enrollment not found.");
    public static readonly Error StudentNotActive = Error.BusinessRule("Enrollment.StudentNotActive", "A student who left cannot be enrolled.");
    public static readonly Error GroupFull = Error.BusinessRule("Enrollment.GroupFull", "The group is full.");
    public static readonly Error AlreadyEnrolled = Error.Conflict("Enrollment.AlreadyEnrolled", "The student is already enrolled in the group.");
    public static readonly Error GroupArchived = Error.BusinessRule("Enrollment.GroupArchived", "An archived group takes no enrollments.");
    public static readonly Error AlreadyEnded = Error.BusinessRule("Enrollment.AlreadyEnded", "The enrollment has already ended.");
    public static readonly Error InvalidEndDate = Error.BusinessRule("Enrollment.InvalidEndDate", "The end date is before the start date.");

    /// <summary>Arguments <c>year</c>, <c>from</c>, <c>to</c>.</summary>
    public static Error BirthYearOutOfRange(int year, BirthYearRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return Error.BusinessRule("Enrollment.BirthYearOutOfRange", "The birth year is outside the group's range.")
            .WithArgument("year", year).WithArgument("from", range.From).WithArgument("to", range.To);
    }

    /// <summary>Argument <c>groupName</c>: the student's other group at that time.</summary>
    public static Error ScheduleConflict(string groupName) =>
        Error.BusinessRule("Enrollment.ScheduleConflict", "The student has another group at this time.").WithArgument("groupName", groupName);
}

using System.Globalization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Students;

public readonly record struct StudentId(Guid Value) : IEntityId<StudentId>
{
    public static StudentId From(Guid value) => new(value);
}

public enum Gender
{
    Female,
    Male,
}

public enum StudentStatus
{
    Active,
    Left,
}

/// <summary>How a guardian is related to a particular student; it lives on the link, not on the guardian.</summary>
public enum GuardianRelationship
{
    Mother,
    Father,
    Other,
}

/// <summary>The editable personal data of a student, validated together because the rules depend on each other.</summary>
public sealed record StudentDetails(
    string FirstName,
    string LastName,
    DateOnly BirthDate,
    Gender Gender,
    PhoneNumber? Phone,
    Email? Email,
    EmergencyContact? EmergencyContact);

public sealed record GuardianLink(GuardianId GuardianId, GuardianRelationship Relationship);

/// <summary>Raised when a student leaves; active enrollments end on <see cref="LeftAt"/> (appendix B §6).</summary>
public sealed record StudentLeft(SchoolId TenantId, StudentId StudentId, DateOnly LeftAt) : IDomainEvent;

/// <summary>
/// Raised when a student's personal data is anonymized (DEC-008, P2-F5). Consumers remove what the student owned
/// elsewhere: guardians no other student needs, stored documents. <see cref="FormerGuardians"/> are the guardians
/// the student was linked to just before.
/// </summary>
public sealed record StudentAnonymized(SchoolId TenantId, StudentId StudentId, IReadOnlyList<GuardianId> FormerGuardians) : IDomainEvent;

/// <summary>
/// A child or adult who trains at the school; a profile, never a user (ADR-0002, appendix B §4.6).
/// A minor needs at least one guardian, an adult needs an own phone, everyone has one emergency contact.
/// The age rules are checked against "today" in the school's time zone whenever the data or the
/// guardians change; turning 18 later does not invalidate a stored student.
/// </summary>
public sealed class Student : AggregateRoot<StudentId>, ITenantScoped
{
    public const int AdultAge = 18;
    public const int MaxGuardians = 4;
    public const int SearchKeyMaxLength = (PersonNames.MaxLength * 2) + 1;

    /// <summary>The first name every anonymized student gets (DEC-363); the last name is "Öğrenci #" and a short id.</summary>
    public const string AnonymizedFirstName = "Anonim";

    private static readonly DateOnly EarliestBirthDate = new(1900, 1, 1);

    private readonly List<StudentGuardian> _guardians = [];

    private Student(StudentId id, SchoolId tenantId)
        : base(id)
    {
        TenantId = tenantId;
        FirstName = null!;
        LastName = null!;
        SearchKey = null!;
        EmergencyContact = null!;
        Status = StudentStatus.Active;
    }

    // For EF Core materialization only.
    private Student()
        : this(default, default)
    {
    }

    public SchoolId TenantId { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    /// <summary>"first last" folded for search: lower case, Turkish letters mapped to ASCII (DEC-013, US-16).</summary>
    public string SearchKey { get; private set; }

    /// <summary>Always set until the student is anonymized (DEC-363).</summary>
    public DateOnly? BirthDate { get; private set; }

    public Gender Gender { get; private set; }

    public StudentStatus Status { get; private set; }

    /// <summary>Set if and only if <see cref="Status"/> is <see cref="StudentStatus.Left"/>.</summary>
    public DateOnly? LeftAt { get; private set; }

    /// <summary>When the retention job anonymized the student (DEC-008, P2-F5); an anonymized student never changes again.</summary>
    public DateTimeOffset? AnonymizedAt { get; private set; }

    public bool IsAnonymized => AnonymizedAt is not null;

    /// <summary>The student's own phone; required for adults.</summary>
    public PhoneNumber? Phone { get; private set; }

    public Email? Email { get; private set; }

    /// <summary>Always set until the student is anonymized (DEC-363).</summary>
    public EmergencyContact? EmergencyContact { get; private set; }

    public IReadOnlyList<StudentGuardian> Guardians => _guardians.AsReadOnly();

    public static Result<Student> Register(SchoolId tenantId, StudentDetails details, IReadOnlyCollection<GuardianLink> guardians, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(guardians);

        var student = new Student(EntityId.New<StudentId>(), tenantId);
        var linked = student.SetGuardians(guardians);
        if (linked.IsFailure)
        {
            return Result.Failure<Student>(linked.Error);
        }

        var updated = student.Update(details, today);
        return updated.IsSuccess ? Result.Success(student) : Result.Failure<Student>(updated.Error);
    }

    /// <summary>Whether someone born on <paramref name="birthDate"/> is under 18 on <paramref name="date"/>; the 18th birthday counts as adult.</summary>
    public static bool IsMinor(DateOnly birthDate, DateOnly date)
    {
        // Compared by (month, day), so a 29 February birthday turns 18 on 1 March in a non-leap year.
        var age = date.Year - birthDate.Year;
        if ((date.Month, date.Day).CompareTo((birthDate.Month, birthDate.Day)) < 0)
        {
            age--;
        }

        return age < AdultAge;
    }

    public bool IsMinorOn(DateOnly date) => BirthDate is { } birthDate && IsMinor(birthDate, date);

    /// <summary>Replaces the personal data and emergency contact; the age rules are checked for <paramref name="today"/>.</summary>
    public Result Update(StudentDetails details, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (IsAnonymized)
        {
            return Result.Failure(StudentErrors.Anonymized);
        }

        var names = PersonNames.Validate(details.FirstName, details.LastName);
        if (names.IsFailure)
        {
            return Result.Failure(names.Error);
        }

        if (!Enum.IsDefined(details.Gender) || details.BirthDate < EarliestBirthDate || details.BirthDate > today)
        {
            return Result.Failure(CommonErrors.FieldInvalid);
        }

        if (details.EmergencyContact is null)
        {
            return Result.Failure(StudentErrors.EmergencyContactRequired);
        }

        var rules = CheckAgeRules(details.BirthDate, details.Phone, _guardians.Count, today);
        if (rules.IsFailure)
        {
            return rules;
        }

        (FirstName, LastName) = names.Value;
        SearchKey = TextNormalization.SearchKey($"{FirstName} {LastName}");
        BirthDate = details.BirthDate;
        Gender = details.Gender;
        Phone = details.Phone;
        Email = details.Email;
        EmergencyContact = details.EmergencyContact;
        return Result.Success();
    }

    /// <summary>Replaces all guardian links; a minor must keep at least one.</summary>
    public Result ReplaceGuardians(IReadOnlyCollection<GuardianLink> guardians, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        if (BirthDate is not { } birthDate)
        {
            return Result.Failure(StudentErrors.Anonymized);
        }

        var rules = CheckAgeRules(birthDate, Phone, guardians.Count, today);
        return rules.IsFailure ? rules : SetGuardians(guardians);
    }

    public Result Leave(DateOnly date)
    {
        if (Status == StudentStatus.Left)
        {
            return Result.Failure(StudentErrors.AlreadyLeft);
        }

        Status = StudentStatus.Left;
        LeftAt = date;
        Raise(new StudentLeft(TenantId, Id, date));
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status != StudentStatus.Left)
        {
            return Result.Failure(StudentErrors.NotLeft);
        }

        if (IsAnonymized)
        {
            return Result.Failure(StudentErrors.Anonymized);
        }

        Status = StudentStatus.Active;
        LeftAt = null;
        return Result.Success();
    }

    /// <summary>
    /// Irreversibly removes the personal data of a student who left on or before <paramref name="cutoff"/> (DEC-008,
    /// DEC-363): the name becomes "Anonim Öğrenci #shortid"; birth date, phone, e-mail, emergency contact and the
    /// guardian links are cleared. Status, leave date and gender stay, so enrollments, attendance and payments keep
    /// their meaning without naming anyone.
    /// </summary>
    public Result Anonymize(DateOnly cutoff, DateTimeOffset now)
    {
        if (IsAnonymized)
        {
            return Result.Failure(StudentErrors.AlreadyAnonymized);
        }

        if (Status != StudentStatus.Left || LeftAt > cutoff)
        {
            return Result.Failure(StudentErrors.NotDueForAnonymization);
        }

        var formerGuardians = _guardians.Select(g => g.GuardianId).ToList();
        FirstName = AnonymizedFirstName;
        LastName = AnonymizedLastName(Id);
        SearchKey = TextNormalization.SearchKey($"{FirstName} {LastName}");
        BirthDate = null;
        Phone = null;
        Email = null;
        EmergencyContact = null;
        _guardians.Clear();
        AnonymizedAt = now;
        Raise(new StudentAnonymized(TenantId, Id, formerGuardians));
        return Result.Success();
    }

    /// <summary>"Öğrenci #" and the last eight hex digits of the id: the random part of a UUIDv7, never its timestamp.</summary>
    public static string AnonymizedLastName(StudentId id) => $"Öğrenci #{id.Value.ToString("N", CultureInfo.InvariantCulture)[^8..]}";

    private static Result CheckAgeRules(DateOnly birthDate, PhoneNumber? phone, int guardianCount, DateOnly today)
    {
        if (IsMinor(birthDate, today))
        {
            return guardianCount > 0 ? Result.Success() : Result.Failure(StudentErrors.GuardianRequired);
        }

        return phone is not null ? Result.Success() : Result.Failure(StudentErrors.PhoneRequiredForAdult);
    }

    private Result SetGuardians(IReadOnlyCollection<GuardianLink> guardians)
    {
        if (guardians.Count > MaxGuardians
            || guardians.Select(g => g.GuardianId).Distinct().Count() != guardians.Count
            || guardians.Any(g => !Enum.IsDefined(g.Relationship)))
        {
            return Result.Failure(CommonErrors.FieldInvalid);
        }

        // Update links in place so an unchanged guardian is not deleted and re-inserted.
        _guardians.RemoveAll(existing => guardians.All(g => g.GuardianId != existing.GuardianId));
        foreach (var link in guardians)
        {
            var existing = _guardians.Find(g => g.GuardianId == link.GuardianId);
            if (existing is null)
            {
                _guardians.Add(new StudentGuardian(TenantId, Id, link.GuardianId, link.Relationship));
            }
            else
            {
                existing.ChangeRelationship(link.Relationship);
            }
        }

        return Result.Success();
    }
}

/// <summary>Link between a student and a guardian, part of the Student aggregate.</summary>
public sealed class StudentGuardian : ITenantScoped
{
    internal StudentGuardian(SchoolId tenantId, StudentId studentId, GuardianId guardianId, GuardianRelationship relationship)
    {
        TenantId = tenantId;
        StudentId = studentId;
        GuardianId = guardianId;
        Relationship = relationship;
    }

    // For EF Core materialization only.
    private StudentGuardian()
    {
    }

    public SchoolId TenantId { get; private set; }

    public StudentId StudentId { get; private set; }

    public GuardianId GuardianId { get; private set; }

    public GuardianRelationship Relationship { get; private set; }

    internal void ChangeRelationship(GuardianRelationship relationship) => Relationship = relationship;
}

public static class StudentErrors
{
    public static readonly Error NotFound = Error.NotFound("Student.NotFound", "Student not found.");
    public static readonly Error GuardianRequired = Error.BusinessRule("Student.GuardianRequired", "A minor needs at least one guardian.");
    public static readonly Error PhoneRequiredForAdult = Error.BusinessRule("Student.PhoneRequiredForAdult", "An adult student needs an own phone number.");
    public static readonly Error EmergencyContactRequired = Error.Validation("Student.EmergencyContactRequired", "The emergency contact is required.");
    public static readonly Error AlreadyLeft = Error.BusinessRule("Student.AlreadyLeft", "The student has already left.");
    public static readonly Error NotLeft = Error.BusinessRule("Student.NotLeft", "Only students who left can be reactivated.");
    public static readonly Error InvalidLeaveDate = Error.BusinessRule("Student.InvalidLeaveDate", "The leave date is before the start of an active enrollment.");
    public static readonly Error Anonymized = Error.BusinessRule("Student.Anonymized", "The student's personal data was anonymized; the record can no longer change.");
    public static readonly Error AlreadyAnonymized = Error.BusinessRule("Student.AlreadyAnonymized", "The student is already anonymized.");
    public static readonly Error NotDueForAnonymization = Error.BusinessRule("Student.NotDueForAnonymization", "Only students who left before the retention period can be anonymized.");
}

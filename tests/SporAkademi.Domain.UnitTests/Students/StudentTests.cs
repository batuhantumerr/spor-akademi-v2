using Microsoft.Extensions.Time.Testing;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Students;

public sealed class StudentTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();
    private static readonly DateOnly Today = new(2027, 1, 14);
    private static readonly PhoneNumber SomePhone = PhoneNumber.Create("0532 111 22 33").Value;
    private static readonly EmergencyContact Contact = EmergencyContact.Create("Ayşe Deneme", PhoneNumber.Create("05321112244").Value, "Teyze").Value;
    private static readonly GuardianLink[] OneGuardian = [new(EntityId.New<GuardianId>(), GuardianRelationship.Mother)];

    private static StudentDetails Details(DateOnly birthDate, PhoneNumber? phone = null, EmergencyContact? contact = null, string firstName = "Şükrü", string lastName = "Yılmaz") =>
        new(firstName, lastName, birthDate, Gender.Male, phone, null, contact ?? Contact);

    [Fact]
    [Trait("Story", "US-13")]
    public void A_registered_student_is_active_with_a_folded_search_key()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1), firstName: "  Şükrü ", lastName: "ÇAĞLAR  Işık"), OneGuardian, Today).Value;

        student.Status.ShouldBe(StudentStatus.Active);
        student.LeftAt.ShouldBeNull();
        student.FirstName.ShouldBe("Şükrü");
        student.LastName.ShouldBe("ÇAĞLAR Işık");
        student.SearchKey.ShouldBe("sukru caglar isik");
        student.TenantId.ShouldBe(School);
        student.Guardians.Single().Relationship.ShouldBe(GuardianRelationship.Mother);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void A_minor_without_a_guardian_is_rejected()
    {
        Student.Register(School, Details(new DateOnly(2015, 5, 1), SomePhone), [], Today).Error.ShouldBe(StudentErrors.GuardianRequired);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void An_adult_needs_an_own_phone_but_no_guardian()
    {
        Student.Register(School, Details(new DateOnly(1990, 5, 1)), [], Today).Error.ShouldBe(StudentErrors.PhoneRequiredForAdult);
        Student.Register(School, Details(new DateOnly(1990, 5, 1), SomePhone), [], Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void An_adult_with_guardians_still_needs_an_own_phone()
    {
        Student.Register(School, Details(new DateOnly(1990, 5, 1)), OneGuardian, Today).Error.ShouldBe(StudentErrors.PhoneRequiredForAdult);
    }

    [Theory]
    [Trait("Story", "US-13")]
    [InlineData("2009-01-14", false)] // 18th birthday today: adult
    [InlineData("2009-01-15", true)] // 18th birthday tomorrow: still a minor
    [InlineData("2009-01-13", false)]
    public void The_18th_birthday_counts_as_adult(string birthDate, bool minor)
    {
        Student.IsMinor(DateOnly.Parse(birthDate, System.Globalization.CultureInfo.InvariantCulture), Today).ShouldBe(minor);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void A_29_february_birthday_turns_18_on_1_march_in_a_non_leap_year()
    {
        var birth = new DateOnly(2008, 2, 29);

        Student.IsMinor(birth, new DateOnly(2026, 2, 28)).ShouldBeTrue();
        Student.IsMinor(birth, new DateOnly(2026, 3, 1)).ShouldBeFalse();
    }

    [Theory]
    [Trait("Story", "US-13")]
    [InlineData(21, 30, true)] // 00:30 on 14 January in Istanbul: the 18th birthday has started
    [InlineData(20, 59, false)] // 23:59 on 13 January in Istanbul: still 17
    public void Age_uses_today_in_the_school_time_zone(int utcHour, int utcMinute, bool adult)
    {
        var school = Domain.Tenancy.School.Create("Okul", Slug.Create("okul-a").Value, "Europe/Istanbul", RetentionPolicy.Default).Value;
        var clock = new FakeTimeProvider(new DateTimeOffset(2027, 1, 13, utcHour, utcMinute, 0, TimeSpan.Zero));

        var registered = Student.Register(school.Id, Details(new DateOnly(2009, 1, 14), SomePhone), [], school.Today(clock));

        registered.IsSuccess.ShouldBe(adult);
        if (!adult)
        {
            registered.Error.ShouldBe(StudentErrors.GuardianRequired);
        }
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void The_emergency_contact_is_required_with_all_parts()
    {
        Student.Register(School, Details(new DateOnly(2015, 5, 1)) with { EmergencyContact = null }, OneGuardian, Today).Error.ShouldBe(StudentErrors.EmergencyContactRequired);
        EmergencyContact.Create(" ", SomePhone, "Anne").Error.ShouldBe(StudentErrors.EmergencyContactRequired);
        EmergencyContact.Create("Ayşe", null, "Anne").Error.ShouldBe(StudentErrors.EmergencyContactRequired);
        EmergencyContact.Create("Ayşe", SomePhone, "").Error.ShouldBe(StudentErrors.EmergencyContactRequired);
        EmergencyContact.Create("Ayşe", SomePhone, new string('a', 51)).Error.ShouldBe(CommonErrors.FieldTooLong);
    }

    [Theory]
    [Trait("Story", "US-13")]
    [InlineData("2027-01-15")]
    [InlineData("1899-12-31")]
    public void Birth_date_must_be_plausible(string birthDate)
    {
        var details = Details(DateOnly.Parse(birthDate, System.Globalization.CultureInfo.InvariantCulture), SomePhone);

        Student.Register(School, details, OneGuardian, Today).Error.ShouldBe(CommonErrors.FieldInvalid);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void Duplicate_or_too_many_guardians_are_invalid()
    {
        var id = EntityId.New<GuardianId>();
        GuardianLink[] duplicate = [new(id, GuardianRelationship.Mother), new(id, GuardianRelationship.Father)];
        var tooMany = Enumerable.Range(0, Student.MaxGuardians + 1).Select(_ => new GuardianLink(EntityId.New<GuardianId>(), GuardianRelationship.Other)).ToArray();

        Student.Register(School, Details(new DateOnly(2015, 5, 1)), duplicate, Today).Error.ShouldBe(CommonErrors.FieldInvalid);
        Student.Register(School, Details(new DateOnly(2015, 5, 1)), tooMany, Today).Error.ShouldBe(CommonErrors.FieldInvalid);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void Replacing_guardians_keeps_a_minor_with_at_least_one()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        var other = EntityId.New<GuardianId>();

        student.ReplaceGuardians([], Today).Error.ShouldBe(StudentErrors.GuardianRequired);
        student.ReplaceGuardians([OneGuardian[0] with { Relationship = GuardianRelationship.Other }, new(other, GuardianRelationship.Father)], Today).IsSuccess.ShouldBeTrue();

        student.Guardians.Select(g => (g.GuardianId, g.Relationship)).ShouldBe(
            [(OneGuardian[0].GuardianId, GuardianRelationship.Other), (other, GuardianRelationship.Father)]);
        student.ReplaceGuardians([new(other, GuardianRelationship.Father)], Today).IsSuccess.ShouldBeTrue();
        student.Guardians.Single().GuardianId.ShouldBe(other);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void An_adult_may_drop_all_guardians()
    {
        var student = Student.Register(School, Details(new DateOnly(1990, 5, 1), SomePhone), OneGuardian, Today).Value;

        student.ReplaceGuardians([], Today).IsSuccess.ShouldBeTrue();
        student.Guardians.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void Update_rechecks_the_rules_and_keeps_data_on_failure()
    {
        var student = Student.Register(School, Details(new DateOnly(1990, 5, 1), SomePhone), [], Today).Value;

        student.Update(Details(new DateOnly(2015, 5, 1), SomePhone), Today).Error.ShouldBe(StudentErrors.GuardianRequired);
        student.Update(Details(new DateOnly(1990, 5, 1)), Today).Error.ShouldBe(StudentErrors.PhoneRequiredForAdult);

        student.BirthDate.ShouldBe(new DateOnly(1990, 5, 1));
        student.Phone.ShouldBe(SomePhone);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void Turning_18_does_not_invalidate_a_stored_minor_until_it_changes()
    {
        var student = Student.Register(School, Details(new DateOnly(2009, 1, 20)), OneGuardian, Today).Value;
        var later = new DateOnly(2027, 2, 1);

        student.IsMinorOn(later).ShouldBeFalse();
        student.Status.ShouldBe(StudentStatus.Active);
        student.Update(Details(new DateOnly(2009, 1, 20)), later).Error.ShouldBe(StudentErrors.PhoneRequiredForAdult);
    }

    [Fact]
    [Trait("Story", "US-15")]
    public void Leave_sets_the_date_and_raises_student_left()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;

        student.Leave(new DateOnly(2027, 1, 10)).IsSuccess.ShouldBeTrue();

        student.Status.ShouldBe(StudentStatus.Left);
        student.LeftAt.ShouldBe(new DateOnly(2027, 1, 10));
        student.DomainEvents.ShouldHaveSingleItem().ShouldBe(new StudentLeft(School, student.Id, new DateOnly(2027, 1, 10)));
    }

    [Fact]
    [Trait("Story", "US-15")]
    public void Leaving_twice_is_rejected()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        student.Leave(Today);
        student.ClearDomainEvents();

        student.Leave(Today).Error.ShouldBe(StudentErrors.AlreadyLeft);
        student.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "US-15")]
    public void Reactivate_clears_left_at_and_only_works_for_students_who_left()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        student.Reactivate().Error.ShouldBe(StudentErrors.NotLeft);

        student.Leave(Today);
        student.Reactivate().IsSuccess.ShouldBeTrue();

        student.Status.ShouldBe(StudentStatus.Active);
        student.LeftAt.ShouldBeNull();
    }

    [Fact]
    [Trait("Story", "P2-F5")]
    public void Anonymize_clears_the_personal_data_of_a_student_who_left_by_the_cutoff_and_raises_an_event()
    {
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1), SomePhone), OneGuardian, Today).Value;
        student.Leave(new DateOnly(2025, 1, 14));
        student.ClearDomainEvents();
        var now = new DateTimeOffset(2027, 1, 14, 2, 0, 0, TimeSpan.Zero);

        student.Anonymize(new DateOnly(2025, 1, 13), now).Error.ShouldBe(StudentErrors.NotDueForAnonymization);
        student.Anonymize(new DateOnly(2025, 1, 14), now).IsSuccess.ShouldBeTrue();

        student.FirstName.ShouldBe("Anonim");
        student.LastName.ShouldBe($"Öğrenci #{student.Id.Value.ToString("N")[^8..]}");
        student.SearchKey.ShouldBe(TextNormalization.SearchKey($"Anonim {student.LastName}"));
        student.BirthDate.ShouldBeNull();
        student.Phone.ShouldBeNull();
        student.Email.ShouldBeNull();
        student.EmergencyContact.ShouldBeNull();
        student.Guardians.ShouldBeEmpty();
        student.AnonymizedAt.ShouldBe(now);
        student.Status.ShouldBe(StudentStatus.Left);
        student.LeftAt.ShouldBe(new DateOnly(2025, 1, 14));
        var raised = student.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StudentAnonymized>();
        raised.FormerGuardians.ShouldBe([OneGuardian[0].GuardianId]);
    }

    [Fact]
    [Trait("Story", "P2-F5")]
    public void Removing_a_guardian_raises_an_event_naming_it_and_keeping_one_raises_none()
    {
        var other = new GuardianLink(EntityId.New<GuardianId>(), GuardianRelationship.Father);
        var student = Student.Register(School, Details(new DateOnly(2015, 5, 1)), [OneGuardian[0], other], Today).Value;

        student.ReplaceGuardians([OneGuardian[0] with { Relationship = GuardianRelationship.Other }], Today).IsSuccess.ShouldBeTrue();
        student.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StudentGuardiansUnlinked>().Removed.ShouldBe([other.GuardianId]);

        student.ClearDomainEvents();
        student.ReplaceGuardians([OneGuardian[0]], Today).IsSuccess.ShouldBeTrue();
        student.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Story", "P2-F5")]
    public void An_active_student_is_never_anonymized_and_an_anonymized_one_never_changes_again()
    {
        var active = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        active.Anonymize(Today, DateTimeOffset.UnixEpoch).Error.ShouldBe(StudentErrors.NotDueForAnonymization);
        active.AnonymizedAt.ShouldBeNull();

        var gone = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        gone.Leave(new DateOnly(2024, 1, 1));
        gone.Anonymize(Today, DateTimeOffset.UnixEpoch).IsSuccess.ShouldBeTrue();

        gone.Anonymize(Today, DateTimeOffset.UnixEpoch.AddDays(1)).Error.ShouldBe(StudentErrors.AlreadyAnonymized);
        gone.AnonymizedAt.ShouldBe(DateTimeOffset.UnixEpoch);
        gone.Reactivate().Error.ShouldBe(StudentErrors.Anonymized);
        gone.Update(Details(new DateOnly(2015, 5, 1)), Today).Error.ShouldBe(StudentErrors.Anonymized);
        gone.ReplaceGuardians(OneGuardian, Today).Error.ShouldBe(StudentErrors.Anonymized);
        gone.Leave(Today).Error.ShouldBe(StudentErrors.AlreadyLeft);
    }

    [Fact]
    [Trait("Story", "P4-06")]
    public void Erasure_on_request_anonymizes_regardless_of_the_retention_period_and_ends_an_active_student_today()
    {
        var active = Student.Register(School, Details(new DateOnly(2015, 5, 1), SomePhone), OneGuardian, Today).Value;
        active.ClearDomainEvents();
        var now = new DateTimeOffset(2027, 1, 14, 9, 0, 0, TimeSpan.Zero);

        active.Erase(Today, now).IsSuccess.ShouldBeTrue();

        active.Status.ShouldBe(StudentStatus.Left);
        active.LeftAt.ShouldBe(Today);
        active.AnonymizedAt.ShouldBe(now);
        active.FirstName.ShouldBe(Student.AnonymizedFirstName);
        active.BirthDate.ShouldBeNull();
        active.Guardians.ShouldBeEmpty();
        active.DomainEvents.Select(e => e.GetType()).ShouldBe([typeof(StudentLeft), typeof(StudentAnonymized)]);

        // A student who left keeps the leave date; the retention period does not matter.
        var left = Student.Register(School, Details(new DateOnly(2015, 5, 1)), OneGuardian, Today).Value;
        left.Leave(new DateOnly(2027, 1, 10));
        left.ClearDomainEvents();
        left.Erase(Today, now).IsSuccess.ShouldBeTrue();
        left.LeftAt.ShouldBe(new DateOnly(2027, 1, 10));
        left.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StudentAnonymized>();

        left.Erase(Today, now).Error.ShouldBe(StudentErrors.AlreadyAnonymized);
    }

    [Fact]
    [Trait("Story", "US-13")]
    public void Guardian_names_are_tidied_and_required()
    {
        var guardian = Guardian.Create(School, " Ayşe ", "Deneme  Yılmaz", SomePhone, null, null).Value;

        guardian.FirstName.ShouldBe("Ayşe");
        guardian.LastName.ShouldBe("Deneme Yılmaz");
        Guardian.Create(School, "", "Deneme", SomePhone, null, null).Error.ShouldBe(CommonErrors.FieldRequired);
        Guardian.Create(School, "Ayşe", new string('a', 101), SomePhone, null, null).Error.ShouldBe(CommonErrors.FieldTooLong);
    }
}

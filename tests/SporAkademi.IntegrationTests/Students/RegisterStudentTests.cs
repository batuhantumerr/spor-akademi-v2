using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Students;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

[Trait("Story", "US-13")]
public sealed class RegisterStudentTests(DatabaseFixture database) : StudentsTest(database)
{
    [Fact]
    public async Task A_student_with_personal_data_and_an_emergency_contact_is_created_active()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", Minor("Şükrü", "Yılmaz"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var student = (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/students/{student.Id}");
        student.Status.ShouldBe(StudentStatus.Active);
        student.LeftAt.ShouldBeNull();
        student.FirstName.ShouldBe("Şükrü");
        student.BirthDate.ShouldBe(new DateOnly(2015, 5, 1));
        student.Gender.ShouldBe(Gender.Male);
        student.EmergencyContact.ShouldBe(new EmergencyContactDto("Ayşe Deneme", "+905321112244", "Teyze"));
        var guardian = student.Guardians.ShouldHaveSingleItem();
        guardian.Relationship.ShouldBe(GuardianRelationship.Mother);
        guardian.Phone.ShouldBe("+905321112233");
        student.Version.ShouldBeGreaterThan(0u);
        (await GetAsync(student.Id)).ShouldBeEquivalentTo(student);
    }

    [Fact]
    public async Task A_minor_without_a_guardian_is_rejected()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", Minor(guardians: []), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Student.GuardianRequired");
    }

    [Fact]
    public async Task An_adult_needs_no_guardian_but_an_own_phone()
    {
        var withPhone = await RegisterAsync(Adult());
        withPhone.Guardians.ShouldBeEmpty();
        withPhone.Phone.ShouldBe("+905332223344");

        var withoutPhone = await Admin.PostAsJsonAsync("/api/v1/students", Adult(phone: null), Ct);
        withoutPhone.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(withoutPhone)).ShouldBe("Student.PhoneRequiredForAdult");
    }

    [Fact]
    public async Task A_missing_emergency_contact_or_part_of_it_is_rejected()
    {
        var missing = await Admin.PostAsJsonAsync("/api/v1/students", new
        {
            firstName = "Ali",
            lastName = "Deneme",
            birthDate = "2015-05-01",
            gender = "Male",
            guardians = new[] { NewGuardian() },
        }, Ct);
        var partial = await Admin.PostAsJsonAsync("/api/v1/students", Minor(emergencyContact: EmergencyContact(relationship: " ")), Ct);

        foreach (var response in new[] { missing, partial })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
            problem.GetProperty("errors").GetProperty("emergencyContact")[0].GetString().ShouldBe("Student.EmergencyContactRequired");
        }
    }

    [Fact]
    public async Task A_sibling_shares_the_existing_guardian_found_by_phone()
    {
        var first = await RegisterAsync(Minor("Ali"));

        // Same phone written differently: normalized to the same E.164 number.
        var sibling = await RegisterAsync(Minor("Veli", guardians: [NewGuardian("+90 (532) 111-22-33", "Mother", "Başka", "İsim")]));

        sibling.Guardians.ShouldHaveSingleItem().GuardianId.ShouldBe(first.Guardians[0].GuardianId);
        sibling.Guardians[0].FirstName.ShouldBe("Ayşe"); // the existing guardian, not a copy with the new data
        var found = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05321112233", Ct);
        found.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task An_existing_guardian_can_be_linked_by_id()
    {
        var first = await RegisterAsync(Minor("Ali"));

        var sibling = await RegisterAsync(Minor("Veli", guardians: [ExistingGuardian(first.Guardians[0].GuardianId, "Father")]));

        sibling.Guardians.ShouldHaveSingleItem().GuardianId.ShouldBe(first.Guardians[0].GuardianId);
        sibling.Guardians[0].Relationship.ShouldBe(GuardianRelationship.Father);
    }

    [Fact]
    public async Task The_same_guardian_twice_is_a_field_error()
    {
        var first = await RegisterAsync(Minor("Ali"));

        var response = await Admin.PostAsJsonAsync("/api/v1/students", Minor("Veli", guardians: [ExistingGuardian(first.Guardians[0].GuardianId), NewGuardian()]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).GetProperty("guardians")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task An_emergency_contact_copied_from_a_guardian_stays_independent()
    {
        // The client copies the chosen guardian's name and phone into the contact fields.
        var student = await RegisterAsync(Minor(emergencyContact: EmergencyContact("Ayşe Deneme", "0532 111 22 33", "Anne")));
        var guardianId = student.Guardians[0].GuardianId;

        var updated = await Admin.PutAsJsonAsync($"/api/v1/guardians/{guardianId}", new
        {
            firstName = "Ayşe",
            lastName = "Deneme",
            phone = "0532 999 88 77",
            version = (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05321112233", Ct)).GetProperty("items")[0].GetProperty("version").GetUInt32(),
        }, Ct);

        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reread = (await GetAsync(student.Id))!;
        reread.EmergencyContact!.Phone.ShouldBe("+905321112233");
        reread.Guardians[0].Phone.ShouldBe("+905329998877");
    }

    [Theory]
    [InlineData(21, 30, HttpStatusCode.Created)] // 00:30 on 15 January in Istanbul (UTC is still the 14th): the 18th birthday
    [InlineData(20, 59, HttpStatusCode.UnprocessableEntity)] // 23:59 on 14 January in Istanbul: still 17
    public async Task Turning_18_today_in_the_school_time_zone_counts_as_adult(int utcHour, int utcMinute, HttpStatusCode expected)
    {
        Api.Clock.SetUtcNow(new DateTimeOffset(2027, 1, 14, utcHour, utcMinute, 0, TimeSpan.Zero));
        using var admin = Api.SchoolAdminClient(School);

        var response = await admin.PostAsJsonAsync("/api/v1/students", Adult(birthDate: "2009-01-15"), Ct);

        response.StatusCode.ShouldBe(expected);
        if (expected != HttpStatusCode.Created)
        {
            (await CodeOf(response)).ShouldBe("Student.GuardianRequired");
        }
    }

    [Fact]
    public async Task A_29_february_birthday_turns_18_on_1_march_in_a_non_leap_year()
    {
        Api.Clock.SetUtcNow(new DateTimeOffset(2030, 2, 28, 12, 0, 0, TimeSpan.Zero));
        using var onFebruary28 = Api.SchoolAdminClient(School);
        var minor = await onFebruary28.PostAsJsonAsync("/api/v1/students", Adult(birthDate: "2012-02-29"), Ct);

        Api.Clock.SetUtcNow(new DateTimeOffset(2030, 3, 1, 12, 0, 0, TimeSpan.Zero));
        using var onMarch1 = Api.SchoolAdminClient(School);
        var adult = await onMarch1.PostAsJsonAsync("/api/v1/students", Adult(birthDate: "2012-02-29"), Ct);

        minor.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(minor)).ShouldBe("Student.GuardianRequired");
        adult.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Concurrent_registrations_with_the_same_new_guardian_share_it()
    {
        var clients = Enumerable.Range(0, 4).Select(_ => Api.SchoolAdminClient(School)).ToList();

        var responses = await Task.WhenAll(clients.Select((c, i) => c.PostAsJsonAsync("/api/v1/students", Minor($"Kardeş{i}"), Ct)));

        responses.Select(r => r.StatusCode).ShouldAllBe(code => code == HttpStatusCode.Created);
        var students = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<StudentDto>(Json, Ct)));
        students.Select(s => s!.Guardians.Single().GuardianId).Distinct().ShouldHaveSingleItem();
        clients.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task Another_school_cannot_update_a_guardian()
    {
        var student = await RegisterAsync(Minor());
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);

        var response = await otherAdmin.PutAsJsonAsync($"/api/v1/guardians/{student.Guardians[0].GuardianId}", new
        {
            firstName = "Hack",
            lastName = "Deneme",
            phone = "0532 111 22 33",
            version = 1,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Guardian.NotFound");
        (await GetAsync(student.Id))!.Guardians[0].FirstName.ShouldBe("Ayşe");
    }

    [Fact]
    public async Task A_guardian_of_another_school_cannot_be_linked()
    {
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        var foreign = await RegisterAsync(Minor("Yabancı"), otherAdmin);

        var response = await Admin.PostAsJsonAsync("/api/v1/students", Minor(guardians: [ExistingGuardian(foreign.Guardians[0].GuardianId)]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Guardian.NotFound");
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_new_guardian_with_a_phone_of_another_school_is_a_separate_guardian()
    {
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        var foreign = await RegisterAsync(Minor("Yabancı"), otherAdmin);

        var mine = await RegisterAsync(Minor("Ali"));

        mine.Guardians[0].GuardianId.ShouldNotBe(foreign.Guardians[0].GuardianId);
    }

    [Fact]
    public async Task Invalid_phones_and_emails_are_field_errors()
    {
        var studentPhone = await Admin.PostAsJsonAsync("/api/v1/students", new
        {
            firstName = "Zeynep",
            lastName = "Örnek",
            birthDate = "1990-03-10",
            gender = "Female",
            phone = "0612 000 00 00",
            email = "yok",
            emergencyContact = EmergencyContact(),
        }, Ct);
        var guardianPhone = await Admin.PostAsJsonAsync("/api/v1/students", Minor(guardians: [NewGuardian("12")]), Ct);
        var contactPhone = await Admin.PostAsJsonAsync("/api/v1/students", Minor(emergencyContact: EmergencyContact(phone: "abc")), Ct);

        (await ErrorsOf(studentPhone)).GetProperty("phone")[0].GetString().ShouldBe("PhoneNumber.Invalid");
        (await ErrorsOf(guardianPhone)).GetProperty("guardians[0].newGuardian.phone")[0].GetString().ShouldBe("PhoneNumber.Invalid");
        (await ErrorsOf(contactPhone)).GetProperty("emergencyContact.phone")[0].GetString().ShouldBe("PhoneNumber.Invalid");
    }

    [Fact]
    public async Task Missing_fields_and_ambiguous_guardian_links_are_reported_per_field()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", new
        {
            firstName = "",
            lastName = new string('a', 101),
            emergencyContact = EmergencyContact(),
            guardians = new object[]
            {
                new { guardianId = Guid.CreateVersion7(), newGuardian = new { firstName = "A", lastName = "B", phone = "05321112233" }, relationship = "Mother" },
                new { newGuardian = new { firstName = "", lastName = "B", phone = "" }, relationship = "Father" },
            },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await ErrorsOf(response);
        errors.GetProperty("firstName")[0].GetString().ShouldBe("Field.Required");
        errors.GetProperty("lastName")[0].GetString().ShouldBe("Field.TooLong");
        errors.GetProperty("birthDate")[0].GetString().ShouldBe("Field.Required");
        errors.GetProperty("guardians[0].guardianId")[0].GetString().ShouldBe("Field.Invalid");
        errors.GetProperty("guardians[1].newGuardian.firstName")[0].GetString().ShouldBe("Field.Required");
        errors.GetProperty("guardians[1].newGuardian.phone")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task A_future_birth_date_is_invalid()
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", Minor(birthDate: "2027-01-15"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).GetProperty("birthDate")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task Coaches_and_other_schools_cannot_reach_students()
    {
        var student = await RegisterAsync(Minor());
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        using var coach = Api.CoachClient(School);

        (await otherAdmin.GetAsync(Url($"/api/v1/students/{student.Id}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.PutAsJsonAsync($"/api/v1/students/{student.Id}/guardians", new { guardians = Array.Empty<object>(), version = student.Version }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-10" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.PostAsync(Url($"/api/v1/students/{student.Id}/reactivate"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otherAdmin.GetFromJsonAsync<JsonElement>("/api/v1/students", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await otherAdmin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05321112233", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await otherAdmin.PutAsJsonAsync($"/api/v1/guardians/{student.Guardians[0].GuardianId}", new { firstName = "X", lastName = "Y", phone = "05321112233", version = 1 }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await coach.GetAsync(Url("/api/v1/students"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(Url($"/api/v1/students/{student.Id}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(Url("/api/v1/guardians?phone=05321112233"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await GetAsync(student.Id))!.Status.ShouldBe(StudentStatus.Active);
    }

    [Fact]
    public async Task An_update_by_another_school_is_not_found()
    {
        var student = await RegisterAsync(Minor());
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);

        var response = await otherAdmin.PutAsJsonAsync($"/api/v1/students/{student.Id}", UpdateOf(student, firstName: "Hack"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Student.NotFound");
    }

    internal static object UpdateOf(StudentDto student, string? firstName = null, string? phone = null, string? birthDate = null, uint? version = null) => new
    {
        firstName = firstName ?? student.FirstName,
        lastName = student.LastName,
        birthDate = birthDate ?? student.BirthDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        gender = student.Gender.ToString(),
        phone = phone ?? student.Phone,
        email = student.Email,
        emergencyContact = new { name = student.EmergencyContact!.Name, phone = student.EmergencyContact.Phone, relationship = student.EmergencyContact.Relationship },
        version = version ?? student.Version,
    };
}

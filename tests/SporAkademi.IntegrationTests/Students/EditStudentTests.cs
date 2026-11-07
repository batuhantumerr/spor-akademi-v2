using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Students;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

/// <summary>Updating students, their guardian links and guardians (US-13), with lost-update protection (API §1.7).</summary>
[Trait("Story", "US-13")]
public sealed class EditStudentTests(DatabaseFixture database) : StudentsTest(database)
{
    [Fact]
    public async Task Update_changes_the_data_and_returns_a_new_version()
    {
        var student = await RegisterAsync(Adult());

        var response = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}", RegisterStudentTests.UpdateOf(student, firstName: "Zehra", phone: "0544 555 66 77"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
        updated.FirstName.ShouldBe("Zehra");
        updated.Phone.ShouldBe("+905445556677");
        updated.Version.ShouldNotBe(student.Version);
    }

    [Fact]
    public async Task A_stale_version_conflicts_even_when_nothing_changes()
    {
        var student = await RegisterAsync(Adult());
        await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}", RegisterStudentTests.UpdateOf(student, firstName: "Zehra"), Ct);

        var stale = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}", RegisterStudentTests.UpdateOf(student), Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(stale)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task A_missing_version_is_a_field_error()
    {
        var student = await RegisterAsync(Adult());

        var response = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}", RegisterStudentTests.UpdateOf(student, version: 0), Ct);

        (await ErrorsOf(response)).GetProperty("version")[0].GetString().ShouldBe("Field.Required");
    }

    [Fact]
    public async Task Update_rechecks_the_age_rules()
    {
        var adult = await RegisterAsync(Adult());

        var removedPhone = await Admin.PutAsJsonAsync($"/api/v1/students/{adult.Id}", new
        {
            firstName = adult.FirstName,
            lastName = adult.LastName,
            birthDate = "1990-03-10",
            gender = "Female",
            phone = (string?)null,
            emergencyContact = EmergencyContact(),
            version = adult.Version,
        }, Ct);
        var nowMinor = await Admin.PutAsJsonAsync($"/api/v1/students/{adult.Id}", RegisterStudentTests.UpdateOf(adult, birthDate: "2015-01-01"), Ct);

        (await CodeOf(removedPhone)).ShouldBe("Student.PhoneRequiredForAdult");
        (await CodeOf(nowMinor)).ShouldBe("Student.GuardianRequired");
        (await GetAsync(adult.Id))!.Phone.ShouldBe("+905332223344");
    }

    [Fact]
    public async Task Replacing_guardians_links_existing_and_new_guardians()
    {
        var student = await RegisterAsync(Minor());
        var mother = student.Guardians[0].GuardianId;

        var response = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}/guardians", new
        {
            guardians = new[] { ExistingGuardian(mother, "Other"), NewGuardian("0555 000 11 22", "Father", "Mehmet") },
            version = student.Version,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
        updated.Guardians.Select(g => (g.FirstName, g.Relationship)).ShouldBe(
            [("Ayşe", GuardianRelationship.Other), ("Mehmet", GuardianRelationship.Father)], ignoreOrder: true);
        updated.Version.ShouldNotBe(student.Version);

        var dropped = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}/guardians", new
        {
            guardians = new[] { ExistingGuardian(mother, "Mother") },
            version = updated.Version,
        }, Ct);
        (await dropped.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!.Guardians.ShouldHaveSingleItem().GuardianId.ShouldBe(mother);

        // DEC-368: the unlinked guardian had no other student, so its personal data is deleted with the link.
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05550001122", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    [Trait("Story", "P2-F5")]
    public async Task An_unlinked_guardian_a_sibling_still_has_is_kept()
    {
        var first = await RegisterAsync(Minor("Bir"));
        var shared = first.Guardians[0].GuardianId;
        await RegisterAsync(Minor("İki", guardians: [ExistingGuardian(shared, "Mother")]));

        var dropped = await Admin.PutAsJsonAsync($"/api/v1/students/{first.Id}/guardians", new
        {
            guardians = new[] { NewGuardian("0555 000 33 44", "Father", "Yeni") },
            version = first.Version,
        }, Ct);

        dropped.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05321112233", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_minor_cannot_lose_all_guardians_but_an_adult_can()
    {
        var minor = await RegisterAsync(Minor());
        var adult = await RegisterAsync(new
        {
            firstName = "Zeynep",
            lastName = "Örnek",
            birthDate = "1990-03-10",
            gender = "Female",
            phone = "0533 222 33 44",
            emergencyContact = EmergencyContact(),
            guardians = new[] { NewGuardian("0555 999 00 11") },
        });

        var minorResponse = await Admin.PutAsJsonAsync($"/api/v1/students/{minor.Id}/guardians", new { guardians = Array.Empty<object>(), version = minor.Version }, Ct);
        var adultResponse = await Admin.PutAsJsonAsync($"/api/v1/students/{adult.Id}/guardians", new { guardians = Array.Empty<object>(), version = adult.Version }, Ct);

        minorResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(minorResponse)).ShouldBe("Student.GuardianRequired");
        adultResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(minor.Id))!.Guardians.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Replacing_guardians_with_a_stale_version_conflicts()
    {
        var student = await RegisterAsync(Minor());
        await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}", RegisterStudentTests.UpdateOf(student, firstName: "Veli"), Ct);

        var stale = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}/guardians", new
        {
            guardians = new[] { NewGuardian("0555 000 11 22", "Father") },
            version = student.Version,
        }, Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetAsync(student.Id))!.Guardians.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_guardian_of_another_school_cannot_be_linked_on_replace()
    {
        var student = await RegisterAsync(Minor());
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        var foreign = await RegisterAsync(Minor("Yabancı", guardians: [NewGuardian("0555 444 33 22")]), otherAdmin);

        var response = await Admin.PutAsJsonAsync($"/api/v1/students/{student.Id}/guardians", new
        {
            guardians = new[] { ExistingGuardian(foreign.Guardians[0].GuardianId) },
            version = student.Version,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Guardian.NotFound");
    }

    [Fact]
    public async Task A_guardian_is_found_by_phone_and_updated_with_its_version()
    {
        await RegisterAsync(Minor());
        var guardian = (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=%2B905321112233", Ct)).GetProperty("items")[0].Deserialize<GuardianDto>(Json)!;

        var response = await Admin.PutAsJsonAsync($"/api/v1/guardians/{guardian.Id}", new
        {
            firstName = "Ayşe",
            lastName = "Yeni",
            phone = guardian.Phone,
            email = (string?)null,
            address = new { line = "Atatürk Cad. 1", district = "Kadıköy", city = "İstanbul" },
            version = guardian.Version,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<GuardianDto>(Json, Ct))!;
        updated.LastName.ShouldBe("Yeni");
        updated.Email.ShouldBeNull();
        updated.Address.ShouldBe(new GuardianAddressDto("Atatürk Cad. 1", "Kadıköy", "İstanbul"));

        var stale = await Admin.PutAsJsonAsync($"/api/v1/guardians/{guardian.Id}", new { firstName = "A", lastName = "B", phone = guardian.Phone, version = guardian.Version }, Ct);
        (await CodeOf(stale)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task A_guardian_cannot_take_another_guardians_phone()
    {
        await RegisterAsync(Minor("Ali"));
        var second = await RegisterAsync(Minor("Veli", guardians: [NewGuardian("0555 000 11 22", "Father", "Mehmet")]));
        var guardian = (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05550001122", Ct)).GetProperty("items")[0].Deserialize<GuardianDto>(Json)!;

        var response = await Admin.PutAsJsonAsync($"/api/v1/guardians/{guardian.Id}", new { firstName = "Mehmet", lastName = "Deneme", phone = "0532 111 22 33", version = guardian.Version }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Guardian.PhoneTaken");
        second.Guardians[0].Phone.ShouldBe("+905550001122");
    }

    [Fact]
    public async Task Guardian_lookup_needs_a_valid_phone_and_returns_nothing_when_unknown()
    {
        (await ErrorsOf(await Admin.GetAsync(Url("/api/v1/guardians"), Ct))).GetProperty("phone")[0].GetString().ShouldBe("Field.Required");
        (await ErrorsOf(await Admin.GetAsync(Url("/api/v1/guardians?phone=123"), Ct))).GetProperty("phone")[0].GetString().ShouldBe("PhoneNumber.Invalid");
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/guardians?phone=05329990000", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_database_maps_a_duplicate_guardian_phone_to_phone_taken()
    {
        // Two concurrent registrations can both pass the lookup; the unique index is the backstop (DEC-133).
        var result = await InScopeAsync(services => services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync<int>(_ =>
        {
            var db = services.GetRequiredService<SporAkademi.Infrastructure.Persistence.AppDbContext>();
            var phone = PhoneNumber.Create("05321112233").Value;
            db.Add(Guardian.Create(School, "Ayşe", "Deneme", phone, null, null).Value);
            db.Add(Guardian.Create(School, "Ayşe", "Kopya", phone, null, null).Value);
            return Task.FromResult(Result.Success(0));
        }, Ct));

        result.Error.ShouldBe(GuardianErrors.PhoneTaken);
    }
}

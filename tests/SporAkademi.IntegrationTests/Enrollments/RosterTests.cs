using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

[Trait("Story", "US-14")]
public sealed class RosterTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    /// <summary>DEC-007 / NFR-PRV-02: everything a coach may see about a student. Nothing else, ever.</summary>
    private static readonly string[] RosterFields = ["birthYear", "emergencyContactPhone", "firstName", "lastName"];

    [Fact]
    public async Task Coach_sees_name_birth_year_and_emergency_phone_of_active_students_of_own_group()
    {
        var group = await GroupAsync("U10 Futbol", 20, Ayse);
        var zeynep = await RegisterStudentAsync("Zeynep", "Yılmaz", "2016-03-01");
        var ali = await RegisterStudentAsync("Ali", "Çelik", "2015-05-01");
        var left = await RegisterStudentAsync("Can", "Ateş");
        await EnrollOkAsync(zeynep, IdOf(group));
        await EnrollOkAsync(ali, IdOf(group));
        await EndAsync(IdOf(await EnrollOkAsync(left, IdOf(group))), Today);
        using var coach = CoachClient(Ayse);

        var roster = await coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/roster", Ct);

        roster.EnumerateArray().Select(r => $"{r.GetProperty("firstName").GetString()} {r.GetProperty("lastName").GetString()} {r.GetProperty("birthYear").GetInt32()}")
            .ShouldBe(["Ali Çelik 2015", "Zeynep Yılmaz 2016"]);
        roster[0].GetProperty("emergencyContactPhone").GetString().ShouldStartWith("+90532");
    }

    [Fact]
    public async Task Roster_json_contains_only_the_allowed_fields()
    {
        var group = await GroupAsync("U10 Futbol", 20, Ayse);
        var student = await RegisterStudentAsync("Ali", "Deneme");
        await EnrollOkAsync(student, IdOf(group));
        using var coach = CoachClient(Ayse);

        var roster = await coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/roster", Ct);
        var full = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);

        var entry = roster.EnumerateArray().ShouldHaveSingleItem();
        entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(RosterFields);
        // None of the admin-only values leaks under any name: guardian, own phone/e-mail, contact name.
        var raw = entry.GetRawText();
        raw.ShouldNotContain(full.GetProperty("guardians")[0].GetProperty("phone").GetString()!);
        raw.ShouldNotContain(full.GetProperty("phone").GetString()!);
        raw.ShouldNotContain(full.GetProperty("email").GetString()!);
        raw.ShouldNotContain(full.GetProperty("emergencyContact").GetProperty("name").GetString()!);
        raw.ShouldNotContain(student.ToString());
        entry.GetProperty("emergencyContactPhone").GetString().ShouldBe(full.GetProperty("emergencyContact").GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Roster_schema_in_the_api_document_has_only_the_allowed_fields()
    {
        var document = JsonNode.Parse(await Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct))!;

        var schema = document["paths"]!["/api/v1/groups/{id}/roster"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!;
        var itemRef = schema["items"]!["$ref"]!.GetValue<string>();
        itemRef.ShouldBe("#/components/schemas/RosterEntryDto");
        var properties = document["components"]!["schemas"]!["RosterEntryDto"]!["properties"]!.AsObject();
        properties.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe(RosterFields);
    }

    [Fact]
    public async Task Coach_gets_not_found_for_a_group_that_is_not_theirs_or_archived()
    {
        var others = await GroupAsync("Mehmet'in Grubu", 20, Mehmet);
        var archived = await GroupAsync("Eski", 20, Ayse);
        await ArchiveAsync(IdOf(archived));
        using var coach = CoachClient(Ayse);

        foreach (var id in new[] { IdOf(others), IdOf(archived), Guid.CreateVersion7() })
        {
            var response = await coach.GetAsync(new Uri($"/api/v1/groups/{id}/roster", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("Group.NotFound");
        }
    }

    [Fact]
    public async Task School_admin_sees_the_roster_of_any_group()
    {
        var group = await GroupAsync("U10 Futbol", 20, Mehmet);
        await EnrollOkAsync(await RegisterStudentAsync(), IdOf(group));

        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/roster", Ct)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Coaches_cannot_read_enrollment_lists_or_student_histories()
    {
        var group = await GroupAsync("U10 Futbol", 20, Ayse);
        var student = await RegisterStudentAsync();
        await EnrollOkAsync(student, IdOf(group));
        using var coach = CoachClient(Ayse);

        (await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/enrollments", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(new Uri($"/api/v1/students/{student}/enrollments", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

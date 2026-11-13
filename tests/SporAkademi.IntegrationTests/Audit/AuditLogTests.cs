using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Infrastructure.Audit;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Audit;

/// <summary>
/// P4-05 (DEC-530 – DEC-537): reads and changes of personal data are recorded with who, when, which record and which
/// fields, never the values. The application role may only add and read entries; old entries go through a function
/// owned by the migrator. Each school sees only its own entries, and only its School Admin.
/// </summary>
[Trait("Story", "P4-05")]
public sealed class AuditLogTests(DatabaseFixture database) : PersonalDataTest(database)
{
    [Fact]
    public async Task Changes_record_the_actor_the_record_and_the_changed_field_names_but_no_values()
    {
        var student = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}", Ct);
        var update = await Admin.PutAsJsonAsync($"/api/v1/students/{Ali}", new
        {
            firstName = "Ahmet",
            lastName = "Yılmaz",
            birthDate = "2015-05-01",
            gender = "Male",
            phone = student.GetProperty("phone").GetString(),
            email = "yeni.adres@example.com",
            emergencyContact = student.GetProperty("emergencyContact"),
            version = student.GetProperty("version").GetUInt32(),
        }, Ct);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = await AuditRowsAsync();

        var created = rows.Single(r => r is { Action: "Create", EntityType: "Student" } && r.EntityId == Ali);
        created.Fields.ShouldContain("FirstName");
        created.Fields.ShouldContain("BirthDate");
        created.Fields.ShouldContain("EmergencyContact");
        created.Fields.ShouldContain("Guardians");
        created.Fields.ShouldNotContain("SearchKey");
        created.UserRole.ShouldBe("SchoolAdmin");
        created.UserId.ShouldNotBeNull();
        created.TenantId.ShouldBe(School.Value);
        rows.Count(r => r is { Action: "Create", EntityType: "Guardian" }).ShouldBe(2);

        var updated = rows.Single(r => r is { Action: "Update", EntityType: "Student" } && r.EntityId == Ali);
        updated.Fields.ShouldBe(["Email", "FirstName"], ignoreOrder: true);

        // Field names only: no name, e-mail address or phone number of anyone is stored.
        var text = await AuditTableTextAsync();
        text.ShouldNotContain("Ahmet");
        text.ShouldNotContain("Yılmaz");
        text.ShouldNotContain("@");
        text.ShouldNotContain("Teyze");
        text.ShouldNotContain("Acil");
    }

    [Fact]
    public async Task Detail_reads_document_opens_and_list_queries_are_recorded_once_each()
    {
        var document = await UploadHealthReportAsync(Ali);
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/students/{Ali}", UriKind.Relative)))
        {
            request.Headers.Add("X-Correlation-Id", "audit-test-correlation");
            (await Admin.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await Admin.GetAsync(new Uri("/api/v1/students?search=ali&status=Active", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Admin.GetAsync(new Uri($"/api/v1/documents/{document}/link", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Admin.GetAsync(new Uri($"/api/v1/students/{Ali}/documents", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // A read that fails (unknown id) records nothing.
        (await Admin.GetAsync(new Uri($"/api/v1/students/{Guid.CreateVersion7()}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var rows = await AuditRowsAsync();

        var read = rows.Where(r => r is { Action: "Read", EntityType: "Student" } && r.EntityId == Ali && r.Fields.Length == 0).ShouldHaveSingleItem();
        read.CorrelationId.ShouldBe("audit-test-correlation");
        read.UserRole.ShouldBe("SchoolAdmin");
        var list = rows.Where(r => r is { Action: "List", EntityType: "Student" }).ShouldHaveSingleItem();
        list.EntityId.ShouldBeNull();
        list.Fields.ShouldBe(["search", "status"], ignoreOrder: true);
        rows.ShouldContain(r => r.Action == "Create" && r.EntityType == "StudentDocument" && r.EntityId == document);
        rows.ShouldContain(r => r.Action == "Read" && r.EntityType == "StudentDocument" && r.EntityId == document);
        rows.ShouldContain(r => r.Action == "Read" && r.EntityType == "Student" && r.EntityId == Ali && r.Fields.SequenceEqual(new[] { "Documents" }));
        rows.Count(r => r.Action == "Read").ShouldBe(3);
    }

    [Fact]
    public async Task A_change_answered_with_the_changed_record_is_not_also_recorded_as_a_read()
    {
        var left = await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/leave", new { date = Today }, Ct);
        left.StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = (await AuditRowsAsync()).Where(r => r.EntityId == Ali).ToList();

        rows.ShouldNotContain(r => r.Action == "Read");
        rows.Single(r => r.Action == "Update").Fields.ShouldBe(["LeftAt", "Status"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_coach_opening_a_photo_is_recorded_with_the_coach_role()
    {
        (await UploadAsync(Ali, "Photo", SporAkademi.Testing.TestFiles.Png(), "foto.png")).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var coach = CoachClient(Ayse);

        (await coach.GetAsync(new Uri($"/api/v1/students/{Ali}/photo", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var read = (await AuditRowsAsync()).Single(r => r.Action == "Read");
        read.EntityType.ShouldBe("Student");
        read.EntityId.ShouldBe(Ali);
        read.Fields.ShouldBe(["Photo"]);
        read.UserId.ShouldBe(Ayse.Id.Value);
        read.UserRole.ShouldBe("Coach");
    }

    [Fact]
    public async Task The_application_role_can_add_and_read_entries_but_never_change_or_delete_them()
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(Ct);

        foreach (var sql in new[]
                 {
                     "UPDATE audit.audit_entries SET fields = '{}'",
                     "DELETE FROM audit.audit_entries",
                     "TRUNCATE audit.audit_entries",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        var privileges = await RowsAsync(
            """
            SELECT p, has_table_privilege('spor_akademi_app', 'audit.audit_entries', p)
            FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER']) p
            ORDER BY p
            """);
        privileges.Where(r => (bool)r[1]!).Select(r => (string)r[0]!).ShouldBe(["INSERT", "SELECT"]);
        (await AuditRowsAsync()).Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Another_schools_entries_are_never_visible()
    {
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);

        (await AuditLogAsync(client: otherAdmin)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await AuditLogAsync()).GetProperty("totalCount").GetInt32().ShouldBeGreaterThan(0);

        // Row level security too: school B's session cannot see school A's rows even by asking for them.
        var visible = await InScopeAsync(sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(otherSchool.Value);
            return sp.GetRequiredService<AppDbContext>().Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM audit.audit_entries WHERE tenant_id = {School.Value}")
                .SingleAsync(Ct);
        });
        visible.ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_school_admin_can_list_the_audit_log()
    {
        using var coach = CoachClient(Ayse);
        using var parent = ParentClient();

        (await coach.GetAsync(new Uri("/api/v1/audit-log", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await parent.GetAsync(new Uri("/api/v1/audit-log", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(new Uri("/api/v1/audit-log/actors", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client.GetAsync(new Uri("/api/v1/audit-log", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_list_filters_by_record_type_user_and_date_and_pages_newest_first()
    {
        (await UploadAsync(Ali, "Photo", SporAkademi.Testing.TestFiles.Png(), "foto.png")).StatusCode.ShouldBe(HttpStatusCode.Created);
        using (var coach = CoachClient(Ayse))
        {
            (await coach.GetAsync(new Uri($"/api/v1/students/{Ali}/photo", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var all = await AuditLogAsync();
        var total = all.GetProperty("totalCount").GetInt32();
        total.ShouldBe((await AuditRowsAsync()).Count);

        var guardians = await AuditLogAsync("?entityType=Guardian");
        guardians.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("entityType").GetString()).ShouldAllBe(t => t == "Guardian");
        guardians.GetProperty("totalCount").GetInt32().ShouldBe(2);

        var byCoach = await AuditLogAsync($"?userId={Ayse.Id.Value}");
        var item = byCoach.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("action").GetString().ShouldBe("Read");
        item.GetProperty("userName").GetString().ShouldBe("Deniz Kaya");
        item.GetProperty("userRole").GetString().ShouldBe("Coach");
        item.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ShouldBe(["Photo"]);

        (await AuditLogAsync("?from=2027-01-14&to=2027-01-14")).GetProperty("totalCount").GetInt32().ShouldBe(total);
        (await AuditLogAsync("?from=2027-01-15")).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await AuditLogAsync("?to=2027-01-13")).GetProperty("totalCount").GetInt32().ShouldBe(0);

        var firstPage = await AuditLogAsync("?pageSize=2&page=1");
        firstPage.GetProperty("items").GetArrayLength().ShouldBe(2);
        firstPage.GetProperty("totalPages").GetInt32().ShouldBe((total + 1) / 2);

        var actors = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/audit-log/actors", Ct);
        actors.EnumerateArray().Select(a => a.GetProperty("userRole").GetString()).ShouldBe(["Coach", "SchoolAdmin"], ignoreOrder: true);

        var invalid = await Admin.GetAsync(new Uri("/api/v1/audit-log?entityType=Payment&from=2027-02-01&to=2027-01-01", UriKind.Relative), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await BodyOf(invalid)).GetProperty("errors");
        errors.TryGetProperty("entityType", out _).ShouldBeTrue();
        errors.TryGetProperty("to", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task The_retention_job_deletes_entries_older_than_two_years_through_the_owners_function()
    {
        await RowsAsync(
            $"""
            INSERT INTO audit.audit_entries (id, tenant_id, occurred_at, action, entity_type, entity_id, fields)
            VALUES ('{Guid.CreateVersion7()}', '{School.Value}', now() - interval '2 years 1 day', 'Read', 'Student', '{Ali}', ARRAY[]::text[]),
                   ('{Guid.CreateVersion7()}', '{School.Value}', now() - interval '2 years' + interval '1 day', 'Read', 'Student', '{Ali}', ARRAY[]::text[])
            """);
        var before = (await AuditRowsAsync()).Count;

        var deleted = await InScopeAsync(sp => sp.GetRequiredService<AuditRetentionJob>().RunAsync(Ct));

        deleted.ShouldBe(1);
        (await AuditRowsAsync()).Count.ShouldBe(before - 1);
        (await RowsAsync("SELECT count(*) FROM audit.audit_entries WHERE occurred_at < now() - interval '2 years'")).Single()[0].ShouldBe(0L);
        // A second run finds nothing more.
        (await InScopeAsync(sp => sp.GetRequiredService<AuditRetentionJob>().RunAsync(Ct))).ShouldBe(0);
    }
}

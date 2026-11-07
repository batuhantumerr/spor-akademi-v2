using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

/// <summary>
/// P2-F5, DEC-008, NFR-PRV-04: the daily job anonymizes departed students once the school's retention period is over.
/// The test clock's today is 2027-01-14 in Istanbul; with the default 24 months the cutoff is 2025-01-14.
/// </summary>
[Trait("Story", "P2-F5")]
public sealed class AnonymizationTests(DatabaseFixture database) : BillingTest(database)
{
    [Fact]
    public async Task A_student_who_left_on_the_cutoff_is_anonymized_one_who_left_a_day_later_waits_and_an_active_one_is_never_touched()
    {
        var onCutoff = await LeftStudentAsync("Sınırda", "2025-01-14");
        var dayAfter = await LeftStudentAsync("Ertesi", "2025-01-15");
        var active = await RegisterStudentAsync("Aktif", birthDate: "1990-01-01");

        (await RunJobAsync()).ShouldBe(1);

        var anonymized = await LoadAsync(onCutoff);
        anonymized.FirstName.ShouldBe("Anonim");
        anonymized.LastName.ShouldBe(Student.AnonymizedLastName(StudentId.From(onCutoff)));
        anonymized.LastName.ShouldStartWith("Öğrenci #");
        anonymized.SearchKey.ShouldBe(TextSearchKey(anonymized));
        anonymized.BirthDate.ShouldBeNull();
        anonymized.Phone.ShouldBeNull();
        anonymized.Email.ShouldBeNull();
        anonymized.EmergencyContact.ShouldBeNull();
        anonymized.Guardians.ShouldBeEmpty();
        anonymized.AnonymizedAt.ShouldBe(Api.Clock.GetUtcNow());
        // What gives the history its meaning stays.
        anonymized.Status.ShouldBe(StudentStatus.Left);
        anonymized.LeftAt.ShouldBe(new DateOnly(2025, 1, 14));
        anonymized.Gender.ShouldBe(Gender.Male);

        (await LoadAsync(dayAfter)).AnonymizedAt.ShouldBeNull();
        var untouched = await LoadAsync(active);
        untouched.AnonymizedAt.ShouldBeNull();
        untouched.FirstName.ShouldBe("Aktif");
        untouched.EmergencyContact.ShouldNotBeNull();

        // One day later the second student is due too; the active one still is not.
        Api.Clock.Advance(TimeSpan.FromDays(1));
        (await RunJobAsync()).ShouldBe(1);
        (await LoadAsync(dayAfter)).AnonymizedAt.ShouldNotBeNull();
        (await LoadAsync(active)).AnonymizedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Running_again_changes_nothing_and_every_run_is_recorded_with_counts_only()
    {
        var student = await LeftStudentAsync("Tekrar", "2024-03-01");
        (await RunJobAsync()).ShouldBe(1);
        var first = await LoadAsync(student);

        using var admin = AdvanceTo(Api.Clock.GetUtcNow().AddHours(1));
        (await RunJobAsync()).ShouldBe(0);

        var second = await LoadAsync(student);
        second.AnonymizedAt.ShouldBe(first.AnonymizedAt);
        second.LastName.ShouldBe(first.LastName);
        var runs = await RunsAsync(admin);
        runs.GetArrayLength().ShouldBe(2);
        // Newest first.
        runs[0].GetProperty("anonymizedCount").GetInt32().ShouldBe(0);
        runs[1].GetProperty("anonymizedCount").GetInt32().ShouldBe(1);
        runs[1].GetProperty("skippedCount").GetInt32().ShouldBe(0);
        runs[1].GetProperty("status").GetString().ShouldBe("Succeeded");
        runs[1].GetProperty("cutoff").GetString().ShouldBe("2025-01-14");
        runs[1].GetProperty("personalDataMonths").GetInt32().ShouldBe(24);
        runs[1].GetProperty("startedAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero));
        runs[1].GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        // A run record names no student.
        runs.GetRawText().ShouldNotContain(student.ToString());
    }

    [Fact]
    public async Task Each_school_applies_its_own_policy_and_sees_only_its_own_runs()
    {
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        using var adminB = Api.SchoolAdminClient(schoolB);
        // School B keeps personal data for ten years.
        var policyB = await PolicyAsync(adminB);
        (await adminB.PutAsJsonAsync("/api/v1/settings/data-retention", new { personalDataMonths = 120, version = policyB.GetProperty("version").GetUInt32() }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var studentA = await LeftStudentAsync("Okula", "2024-06-01");
        var studentB = await LeftStudentAsync("Okulb", "2024-06-01", adminB);

        (await RunJobAsync()).ShouldBe(1);

        (await LoadAsync(studentA)).AnonymizedAt.ShouldNotBeNull();
        (await LoadAsync(studentB)).AnonymizedAt.ShouldBeNull();
        var runsA = await RunsAsync(Admin);
        var runsB = await RunsAsync(adminB);
        runsA.GetArrayLength().ShouldBe(1);
        runsB.GetArrayLength().ShouldBe(1);
        runsA[0].GetProperty("anonymizedCount").GetInt32().ShouldBe(1);
        runsB[0].GetProperty("anonymizedCount").GetInt32().ShouldBe(0);
        runsB[0].GetProperty("personalDataMonths").GetInt32().ShouldBe(120);
        runsB[0].GetProperty("id").GetGuid().ShouldNotBe(runsA[0].GetProperty("id").GetGuid());
        var stored = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<AnonymizationRun>().IgnoreQueryFilters().ToListAsync(Ct));
        stored.Select(r => r.TenantId).ShouldBe([School, schoolB], ignoreOrder: true);
    }

    [Fact]
    public async Task Payments_and_enrollments_are_kept_with_amounts_and_dates_but_name_no_one()
    {
        var (student, _, enrollment) = await EnrolledAsync(firstName: "Ödeyen");
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "750.00"));
        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = "2027-01-14" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Two years later the student is due.
        using var admin = AdvanceTo(new DateTimeOffset(2029, 1, 14, 12, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(1);

        var paid = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment}", Ct);
        AmountOf(paid.GetProperty("amount")).ShouldBe("750.00");
        paid.GetProperty("period").GetString().ShouldBe("2027-01");
        paid.GetProperty("paidOn").GetString().ShouldBe("2027-01-10");
        paid.GetProperty("status").GetString().ShouldBe("Recorded");
        paid.GetProperty("studentId").GetGuid().ShouldBe(student);
        paid.GetProperty("studentFirstName").GetString().ShouldBe("Anonim");
        paid.GetRawText().ShouldNotContain("Ödeyen");
        var enrollments = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}/enrollments", Ct);
        enrollments.GetRawText().ShouldContain(enrollment.ToString());
        (await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Payment>().IgnoreQueryFilters().CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task A_guardian_only_the_anonymized_student_had_is_deleted_one_a_sibling_still_needs_stays()
    {
        var shared = await RegisterAsync(Minor("Kardeşbir", guardians: [Guardian("0532 700 00 01", "Ortak")]));
        var sharedGuardian = shared.GetProperty("guardians")[0].GetProperty("guardianId").GetGuid();
        await RegisterAsync(Minor("Kardeşiki", guardians: [new { guardianId = sharedGuardian, relationship = "Father" }]));
        var alone = await RegisterAsync(Minor("Yalnız", guardians: [Guardian("0532 700 00 02", "Tekveli")]));
        var aloneGuardian = alone.GetProperty("guardians")[0].GetProperty("guardianId").GetGuid();
        await LeaveAsync(IdOf(shared), "2024-01-10");
        await LeaveAsync(IdOf(alone), "2024-01-10");

        (await RunJobAsync()).ShouldBe(2);

        var guardians = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Guardian>().IgnoreQueryFilters().Select(g => g.Id.Value).ToListAsync(Ct));
        guardians.ShouldBe([sharedGuardian]);
        guardians.ShouldNotContain(aloneGuardian);
        var links = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<StudentGuardian>().IgnoreQueryFilters().ToListAsync(Ct));
        links.ShouldHaveSingleItem().GuardianId.Value.ShouldBe(sharedGuardian);
    }

    [Fact]
    public async Task An_anonymized_student_cannot_be_edited_reactivated_or_enrolled()
    {
        var student = await LeftStudentAsync("Kapalı", "2024-01-10");
        await RunJobAsync();
        var dto = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        dto.GetProperty("anonymizedAt").ValueKind.ShouldBe(JsonValueKind.String);
        dto.GetProperty("birthDate").ValueKind.ShouldBe(JsonValueKind.Null);
        dto.GetProperty("emergencyContact").ValueKind.ShouldBe(JsonValueKind.Null);
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var reactivated = await Admin.PostAsync(new Uri($"/api/v1/students/{student}/reactivate", UriKind.Relative), null, Ct);
        var edited = await Admin.PutAsJsonAsync($"/api/v1/students/{student}", new
        {
            firstName = "Yeni",
            lastName = "Ad",
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = "0532 711 11 11", relationship = "Teyze" },
            version = dto.GetProperty("version").GetUInt32(),
        }, Ct);
        var enrolled = await EnrollAsync(student, group);

        reactivated.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOfAsync(reactivated)).ShouldBe("Student.Anonymized");
        edited.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOfAsync(edited)).ShouldBe("Student.Anonymized");
        (await CodeOfAsync(enrolled)).ShouldBe("Enrollment.StudentNotActive");
    }

    [Fact]
    public async Task A_student_reactivated_after_being_listed_is_left_alone()
    {
        var student = await LeftStudentAsync("Dönen", "2024-01-10");
        var job = Api.Services.GetRequiredService<TenantJobRunner>();
        await job.ForEachSchoolAsync("test", async (services, school, ct) =>
        {
            var anonymizer = services.GetRequiredService<Application.Students.IStudentAnonymizer>();
            var plan = await anonymizer.PlanAsync(ct);
            var due = await anonymizer.NextDueAsync(plan, [], 10, ct);
            due.ShouldBe([StudentId.From(student)]);

            // The admin reactivates the student between listing and anonymizing.
            (await Admin.PostAsync(new Uri($"/api/v1/students/{student}/reactivate", UriKind.Relative), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            var batch = await job.ForSchoolAsync(school, (sp, c) => sp.GetRequiredService<Application.Students.IStudentAnonymizer>().AnonymizeAsync(plan, due, c), ct);
            batch.Anonymized.ShouldBeEmpty();
            batch.Skipped.ShouldBeEmpty();
        }, Ct);

        var loaded = await LoadAsync(student);
        loaded.AnonymizedAt.ShouldBeNull();
        loaded.Status.ShouldBe(StudentStatus.Active);
    }

    [Fact]
    public async Task A_concurrent_change_to_a_loaded_student_is_detected_by_its_version_and_retried_next_run()
    {
        var student = await LeftStudentAsync("Yarış", "2024-01-10");
        var other = await LeftStudentAsync("Komşu", "2024-01-10");
        var runner = Api.Services.GetRequiredService<TenantJobRunner>();

        await runner.ForSchoolAsync(School, async (services, ct) =>
        {
            var anonymizer = services.GetRequiredService<Application.Students.IStudentAnonymizer>();
            var plan = await anonymizer.PlanAsync(ct);
            // A raw UPDATE inside the batch transaction's window: the row's xmin changes after EF loaded it.
            var db = services.GetRequiredService<AppDbContext>();
            db.SavingChanges += (_, _) => TouchStudentOutsideTheTransaction(student);
            var batch = await anonymizer.AnonymizeAsync(plan, [StudentId.From(student), StudentId.From(other)], ct);

            // The batch conflicted and was retried one student at a time: the other student is anonymized, the
            // touched one (changed again on every attempt) is skipped instead of overwriting the concurrent change.
            batch.Anonymized.ShouldBe([StudentId.From(other)]);
            batch.Skipped.ShouldBe([StudentId.From(student)]);
            return 0;
        }, Ct);

        (await LoadAsync(other)).AnonymizedAt.ShouldNotBeNull();
        (await LoadAsync(student)).AnonymizedAt.ShouldBeNull();
        // The next run picks it up.
        (await RunJobAsync()).ShouldBe(1);
        (await LoadAsync(student)).AnonymizedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_school_admin_reads_and_changes_the_retention_period_within_bounds()
    {
        var policy = await PolicyAsync(Admin);
        policy.GetProperty("personalDataMonths").GetInt32().ShouldBe(24);
        policy.GetProperty("paymentYears").GetInt32().ShouldBe(10);
        policy.GetProperty("minPersonalDataMonths").GetInt32().ShouldBe(6);
        policy.GetProperty("maxPersonalDataMonths").GetInt32().ShouldBe(120);
        var version = policy.GetProperty("version").GetUInt32();

        var tooShort = await Admin.PutAsJsonAsync("/api/v1/settings/data-retention", new { personalDataMonths = 5, version }, Ct);
        var tooLong = await Admin.PutAsJsonAsync("/api/v1/settings/data-retention", new { personalDataMonths = 121, version }, Ct);
        var changed = await Admin.PutAsJsonAsync("/api/v1/settings/data-retention", new { personalDataMonths = 6, version }, Ct);
        var stale = await Admin.PutAsJsonAsync("/api/v1/settings/data-retention", new { personalDataMonths = 12, version }, Ct);
        using var coach = CoachClient(Ayse);
        var asCoach = await coach.GetAsync(new Uri("/api/v1/settings/data-retention", UriKind.Relative), Ct);

        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await changed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        updated.GetProperty("personalDataMonths").GetInt32().ShouldBe(6);
        updated.GetProperty("paymentYears").GetInt32().ShouldBe(10);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(stale)).ShouldBe("Concurrency.Conflict");
        asCoach.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Six months: a student who left in mid-July 2026 is due on 14 January 2027.
        var student = await LeftStudentAsync("Kısa", "2026-07-14");
        (await RunJobAsync()).ShouldBe(1);
        (await LoadAsync(student)).AnonymizedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_guardian_linked_by_a_concurrent_registration_is_kept_and_the_run_does_not_fail()
    {
        var gone = await LeftStudentAsync("Gidiyor", "2024-01-10");
        var guardian = (await LoadAsync(gone)).Guardians.ShouldHaveSingleItem().GuardianId.Value;
        var stays = await RegisterStudentAsync("Kalıyor");

        // Another transaction links the guardian (as registration does: lock, then insert the link) and has not committed.
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var linking = await connection.BeginTransactionAsync(Ct);
        await ExecuteAsync(connection, "SELECT id FROM students.guardians WHERE id = @g FOR SHARE", ("g", guardian));
        await ExecuteAsync(
            connection,
            "INSERT INTO students.student_guardians (tenant_id, student_id, guardian_id, relationship, created_at, updated_at) VALUES (@t, @s, @g, 'Father', now(), now())",
            ("t", School.Value),
            ("s", stays),
            ("g", guardian));

        var run = RunJobAsync();
        await WaitForALockWaitAsync();
        await linking.CommitAsync(Ct);

        (await run).ShouldBe(1);
        var guardians = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Guardian>().IgnoreQueryFilters().Select(g => g.Id.Value).ToListAsync(Ct));
        guardians.ShouldContain(guardian);
        (await LoadAsync(stays)).Guardians.Select(g => g.GuardianId.Value).ShouldContain(guardian);
        (await LoadAsync(gone)).AnonymizedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Guardians_left_without_students_are_deleted_by_the_nightly_run()
    {
        var student = await RegisterStudentAsync("Eski");
        var guardian = (await LoadAsync(student)).Guardians.ShouldHaveSingleItem().GuardianId.Value;
        // An orphan from before DEC-368 (unlinking did not delete guardians then).
        await using (var connection = new NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await ExecuteAsync(connection, "DELETE FROM students.student_guardians WHERE guardian_id = @g", ("g", guardian));
        }

        (await RunJobAsync()).ShouldBe(0);

        (await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Guardian>().IgnoreQueryFilters().AnyAsync(g => g.Id == GuardianId.From(guardian), Ct)))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_run_is_recorded_as_failed_and_the_job_fails()
    {
        await LeftStudentAsync("Hatalı", "2024-01-10");
        await using var failing = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IStudentAnonymizer>(sp => new FailingAnonymizer(ActivatorUtilities.CreateInstance<StudentAnonymizer>(sp)))));

        await using (var scope = failing.Services.CreateAsyncScope())
        {
            await Should.ThrowAsync<TenantJobException>(scope.ServiceProvider.GetRequiredService<StudentAnonymizationJob>().RunAsync(Ct));
        }

        var run = (await RunsAsync(Admin)).EnumerateArray().ShouldHaveSingleItem();
        run.GetProperty("status").GetString().ShouldBe("Failed");
        run.GetProperty("anonymizedCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_cutoff_uses_today_in_the_school_time_zone_not_the_utc_date()
    {
        var student = await LeftStudentAsync("Gece", "2025-01-15");
        // 21:30 UTC on 14 January is already 00:30 on 15 January in Istanbul: the cutoff is 2025-01-15.
        Api.Clock.SetUtcNow(new DateTimeOffset(2027, 1, 14, 21, 30, 0, TimeSpan.Zero));

        (await RunJobAsync()).ShouldBe(1);

        (await LoadAsync(student)).AnonymizedAt.ShouldNotBeNull();
        var run = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<AnonymizationRun>().IgnoreQueryFilters().SingleAsync(Ct));
        run.Cutoff.ShouldBe(new DateOnly(2025, 1, 15));
    }

    [Fact]
    public async Task The_database_rejects_cleared_personal_data_without_anonymization()
    {
        var student = await RegisterStudentAsync("Kural");
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);

        var noBirthDate = await Should.ThrowAsync<PostgresException>(
            ExecuteAsync(connection, "UPDATE students.students SET birth_date = NULL WHERE id = @s", ("s", student)));
        var noContact = await Should.ThrowAsync<PostgresException>(
            ExecuteAsync(connection, "UPDATE students.students SET emergency_contact_name = NULL WHERE id = @s", ("s", student)));
        var activeAnonymized = await Should.ThrowAsync<PostgresException>(
            ExecuteAsync(connection, "UPDATE students.students SET anonymized_at = now() WHERE id = @s", ("s", student)));

        noBirthDate.ConstraintName.ShouldBe("ck_students_personal_data");
        noContact.ConstraintName.ShouldBe("ck_students_personal_data");
        activeAnonymized.ConstraintName.ShouldBe("ck_students_anonymized_left");
    }

    [Fact]
    public async Task A_run_works_through_several_batches()
    {
        var students = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            students.Add(await LeftStudentAsync($"Parti{(char)('a' + i)}", "2024-01-10"));
        }

        await using var small = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:AnonymizationBatchSize"] = "2" })));
        await using (var scope = small.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<StudentAnonymizationJob>().RunAsync(Ct)).ShouldBe(5);
        }

        foreach (var student in students)
        {
            (await LoadAsync(student)).AnonymizedAt.ShouldNotBeNull();
        }

        (await RunsAsync(Admin)).EnumerateArray().ShouldHaveSingleItem().GetProperty("anonymizedCount").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task Overlapping_runs_do_not_both_work()
    {
        await LeftStudentAsync("Çakışma", "2024-01-10");
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await ExecuteAsync(connection, $"SELECT pg_advisory_lock({StudentAnonymizationJob.RunLockKey})");

        (await RunJobAsync()).ShouldBe(0);
        (await RunsAsync(Admin)).GetArrayLength().ShouldBe(0);

        await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({StudentAnonymizationJob.RunLockKey})");
        (await RunJobAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_impact_of_a_shorter_period_counts_the_students_it_would_make_due()
    {
        await LeftStudentAsync("Yakın", "2026-03-01");
        await LeftStudentAsync("Uzak", "2024-03-01");

        var current = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/settings/data-retention/impact", Ct);
        var shorter = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/settings/data-retention/impact?personalDataMonths=6", Ct);
        var invalid = await Admin.GetAsync(new Uri("/api/v1/settings/data-retention/impact?personalDataMonths=3", UriKind.Relative), Ct);

        current.GetProperty("personalDataMonths").GetInt32().ShouldBe(24);
        current.GetProperty("dueCount").GetInt32().ShouldBe(1);
        shorter.GetProperty("cutoff").GetString().ShouldBe("2026-07-14");
        shorter.GetProperty("dueCount").GetInt32().ShouldBe(2);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Waits until some session waits for a lock (the job blocked on the guardian row).</summary>
    private async Task WaitForALockWaitAsync()
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        for (var i = 0; i < 200; i++)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE NOT granted", connection);
            if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException("The job never waited for the guardian lock.");
    }

    /// <summary>Fails every batch, like a database outage in the middle of a run.</summary>
    private sealed class FailingAnonymizer(IStudentAnonymizer inner) : IStudentAnonymizer
    {
        public Task<AnonymizationPlan> PlanAsync(CancellationToken cancellationToken) => inner.PlanAsync(cancellationToken);

        public Task<IReadOnlyList<StudentId>> NextDueAsync(AnonymizationPlan plan, IReadOnlyCollection<StudentId> seen, int batchSize, CancellationToken cancellationToken) =>
            inner.NextDueAsync(plan, seen, batchSize, cancellationToken);

        public Task<AnonymizationBatchResult> AnonymizeAsync(AnonymizationPlan plan, IReadOnlyList<StudentId> students, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated failure.");

        public Task RecordRunAsync(AnonymizationRun run, CancellationToken cancellationToken) => inner.RecordRunAsync(run, cancellationToken);

        public Task<int> DeleteUnlinkedGuardiansAsync(int limit, CancellationToken cancellationToken) => inner.DeleteUnlinkedGuardiansAsync(limit, cancellationToken);
    }

    private async Task<Guid> LeftStudentAsync(string firstName, string leftOn, HttpClient? client = null)
    {
        var student = await RegisterStudentAsync(firstName, birthDate: "2010-05-01", client: client);
        await LeaveAsync(student, leftOn, client);
        return student;
    }

    private async Task LeaveAsync(Guid student, string date, HttpClient? client = null) =>
        (await (client ?? Admin).PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task<JsonElement> RegisterAsync(object request)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await BodyOf(response);
    }

    private static object Guardian(string phone, string lastName) =>
        new { newGuardian = new { firstName = "Veli", lastName, phone, email = "veli@example.com" }, relationship = "Mother" };

    private static object Minor(string firstName, object[] guardians) => new
    {
        firstName,
        lastName = "Deneme",
        birthDate = "2015-05-01",
        gender = "Male",
        emergencyContact = new { name = "Acil Kişi", phone = "0532 799 99 99", relationship = "Teyze" },
        guardians,
    };

    private Task<int> RunJobAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<StudentAnonymizationJob>().RunAsync(Ct));

    private Task<Student> LoadAsync(Guid id) =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Student>().IgnoreQueryFilters()
            .Include(s => s.Guardians)
            .SingleAsync(s => s.Id == StudentId.From(id), Ct));

    private static async Task<JsonElement> PolicyAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/data-retention", Ct);

    private static async Task<JsonElement> RunsAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/data-retention/runs", Ct);

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static string TextSearchKey(Student student) =>
        Domain.SharedKernel.TextNormalization.SearchKey($"{student.FirstName} {student.LastName}");

    /// <summary>Bumps the row's xmin from another connection, like an admin saving the student at that moment.</summary>
    private void TouchStudentOutsideTheTransaction(Guid student)
    {
        using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("UPDATE students.students SET updated_at = updated_at WHERE id = @id AND anonymized_at IS NULL", connection);
        command.Parameters.AddWithValue("id", student);
        command.ExecuteNonQuery();
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Audit;

/// <summary>
/// P4-06 (NFR-PRV-05; DEC-538 – DEC-544): the School Admin exports a student's data in a machine-readable
/// form and erases it on request, whatever the retention period. Both are recorded in the audit log.
/// </summary>
[Trait("Story", "P4-06")]
public sealed class DataSubjectRequestTests(DatabaseFixture database) : PersonalDataTest(database)
{
    [Fact]
    public async Task The_export_holds_the_student_guardians_enrollments_attendance_payments_and_document_links()
    {
        using (var coach = CoachClient(Ayse))
        {
            await SaveOkAsync(coach, ThisMorning, (Ali, "Present"), (Veli, "Absent"));
        }

        await PayJanuaryAsync(Ali);
        var document = await UploadHealthReportAsync(Ali);

        var response = await Admin.GetAsync(new Uri($"/api/v1/students/{Ali}/data-export", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBe($"ogrenci-verisi-{Ali}.json");
        var export = await BodyOf(response);
        export.GetProperty("format").GetString().ShouldBe("spor-akademi.student-data-export/v1");
        export.GetProperty("exportedAt").GetDateTimeOffset().ShouldBe(Api.Clock.GetUtcNow());
        var student = export.GetProperty("student");
        student.GetProperty("id").GetGuid().ShouldBe(Ali);
        student.GetProperty("firstName").GetString().ShouldBe("Ali");
        student.GetProperty("lastName").GetString().ShouldBe("Yılmaz");
        student.GetProperty("emergencyContact").GetProperty("relationship").GetString().ShouldBe("Teyze");
        var guardian = export.GetProperty("guardians").EnumerateArray().ShouldHaveSingleItem();
        guardian.GetProperty("firstName").GetString().ShouldBe("Veli");
        guardian.GetProperty("relationship").GetString().ShouldBe("Mother");
        var enrollment = export.GetProperty("enrollments").EnumerateArray().ShouldHaveSingleItem();
        enrollment.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        enrollment.GetProperty("startDate").GetString().ShouldBe("2027-01-04");
        var attendance = export.GetProperty("attendance").EnumerateArray().ShouldHaveSingleItem();
        attendance.GetProperty("date").GetString().ShouldBe("2027-01-14");
        attendance.GetProperty("status").GetString().ShouldBe("Present");
        var payment = export.GetProperty("payments").EnumerateArray().ShouldHaveSingleItem();
        payment.GetProperty("period").GetString().ShouldBe("2027-01");
        payment.GetProperty("amount").GetProperty("amount").GetString().ShouldBe("1000.00");
        var file = export.GetProperty("documents").EnumerateArray().ShouldHaveSingleItem();
        file.GetProperty("id").GetGuid().ShouldBe(document);
        file.GetProperty("type").GetString().ShouldBe("HealthReport");
        file.GetProperty("downloadUrl").GetString().ShouldNotBeNullOrEmpty();
        // The link's lifetime follows the storage's own clock (DEC-323), not the test clock.
        file.GetProperty("linkExpiresAt").ValueKind.ShouldBe(JsonValueKind.String);

        var exported = (await AuditRowsAsync()).Single(r => r.Action == "Export");
        exported.EntityType.ShouldBe("Student");
        exported.EntityId.ShouldBe(Ali);
        exported.UserRole.ShouldBe("SchoolAdmin");
    }

    [Fact]
    public async Task The_export_holds_free_text_of_payments_refunds_and_online_payment_attempts()
    {
        await DefineJanuaryFeeAsync();
        var cash = await PayCashAsync(Ali);
        (await Admin.PostAsJsonAsync($"/api/v1/payments/{cash}/cancel", new { reason = "Yanlış öğrenciye girildi" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var (online, token) = await StartOnlineAsync(Ali);
        var paid = await CompleteOnlineAsync(online, token);
        using (var refund = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/payments/{paid}/refund", UriKind.Relative)))
        {
            refund.Content = JsonContent.Create(new { reason = "Kayıt erken bitti" });
            refund.Headers.Add("Idempotency-Key", "iade-export-1");
            (await Admin.SendAsync(refund, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var export = await BodyOf(await Admin.GetAsync(new Uri($"/api/v1/students/{Ali}/data-export", UriKind.Relative), Ct));

        var cancelled = export.GetProperty("payments").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == cash);
        cancelled.GetProperty("cancelReason").GetString().ShouldBe("Yanlış öğrenciye girildi");
        var refunded = export.GetProperty("refunds").EnumerateArray().ShouldHaveSingleItem();
        refunded.GetProperty("paymentId").GetGuid().ShouldBe(paid);
        refunded.GetProperty("reason").GetString().ShouldBe("Kayıt erken bitti");
        var attempt = export.GetProperty("onlinePayments").EnumerateArray().ShouldHaveSingleItem();
        attempt.GetProperty("id").GetGuid().ShouldBe(online);
        attempt.GetProperty("status").GetString().ShouldBe("Succeeded");
        attempt.TryGetProperty("checkoutUrl", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Erasure_waits_while_an_online_payment_is_still_open_at_the_provider()
    {
        await DefineJanuaryFeeAsync();
        var (online, token) = await StartOnlineAsync(Ali);

        var refused = await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/erasure", new { reason = "DataSubjectRequest" }, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(refused)).ShouldBe("Student.OpenPaymentActivity");
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}", Ct)).GetProperty("firstName").GetString().ShouldBe("Ali");
        (await AuditRowsAsync()).ShouldNotContain(r => r.Action == "Erase");

        await CompleteOnlineAsync(online, token);
        (await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/erasure", new { reason = "DataSubjectRequest" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("DataSubjectRequest,ConsentWithdrawn")]
    [InlineData(" DataSubjectRequest")]
    [InlineData("datasubjectrequest")]
    [InlineData("0")]
    public async Task Erasure_accepts_only_one_exact_reason_code(string reason)
    {
        var response = await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/erasure", new { reason }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("Field.Invalid");
        (await AuditRowsAsync()).ShouldNotContain(r => r.Action == "Erase");
    }

    [Fact]
    public async Task Only_the_school_admin_of_the_students_school_exports()
    {
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);
        using var coach = CoachClient(Ayse);
        using var parent = ParentClient();
        var url = new Uri($"/api/v1/students/{Ali}/data-export", UriKind.Relative);

        (await coach.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await parent.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var other = await otherAdmin.GetAsync(url, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(other)).ShouldBe("Student.NotFound");
        (await AuditRowsAsync()).ShouldNotContain(r => r.Action == "Export");
    }

    [Fact]
    public async Task Erasure_ends_an_active_student_today_and_leaves_exactly_what_anonymization_leaves()
    {
        var payment = await PayJanuaryAsync(Ali);
        var document = await UploadHealthReportAsync(Ali);
        var guardian = (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}", Ct))
            .GetProperty("guardians")[0].GetProperty("guardianId").GetGuid();

        var response = await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/erasure", new { reason = "DataSubjectRequest" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var student = await BodyOf(response);
        student.GetProperty("firstName").GetString().ShouldBe("Anonim");
        student.GetProperty("status").GetString().ShouldBe("Left");
        student.GetProperty("leftAt").GetString().ShouldBe(Today);
        student.GetProperty("anonymizedAt").ValueKind.ShouldBe(JsonValueKind.String);
        student.GetProperty("birthDate").ValueKind.ShouldBe(JsonValueKind.Null);
        student.GetProperty("guardians").GetArrayLength().ShouldBe(0);

        var enrollment = (await StudentEnrollmentsAsync(Ali)).EnumerateArray().Single();
        enrollment.GetProperty("endDate").GetString().ShouldBe(Today);
        (await CountAsync($"SELECT count(*) FROM students.guardians WHERE id = '{guardian}'")).ShouldBe(0);
        (await CountAsync($"SELECT count(*) FROM students.student_documents WHERE student_id = '{Ali}'")).ShouldBe(0);
        // Payments stay for the legal period, now pointing at an anonymous student (DEC-363).
        (await CountAsync($"SELECT count(*) FROM billing.payments WHERE id = '{payment}' AND student_id = '{Ali}'")).ShouldBe(1);
        // Veli is untouched.
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Veli}", Ct)).GetProperty("firstName").GetString().ShouldBe("Veli");

        var rows = await AuditRowsAsync();
        var erased = rows.Single(r => r.Action == "Erase");
        erased.EntityType.ShouldBe("Student");
        erased.EntityId.ShouldBe(Ali);
        erased.Reason.ShouldBe("DataSubjectRequest");
        erased.UserRole.ShouldBe("SchoolAdmin");
        erased.Fields.ShouldContain("FirstName");
        erased.Fields.ShouldContain("BirthDate");
        erased.Fields.ShouldContain("EmergencyContact");
        erased.Fields.ShouldContain("Guardians");
        erased.Fields.ShouldContain("Status");
        rows.ShouldContain(r => r.Action == "Delete" && r.EntityType == "Guardian" && r.EntityId == guardian);
        rows.ShouldContain(r => r.Action == "Delete" && r.EntityType == "StudentDocument" && r.EntityId == document);
        // The erasure is one entry, not an extra "Update" of the same student.
        rows.ShouldNotContain(r => r.Action == "Update" && r.EntityId == Ali);

        var again = await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/erasure", new { reason = "DataSubjectRequest" }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("Student.AlreadyAnonymized");
    }

    [Fact]
    public async Task Erasure_needs_a_known_reason_and_the_school_admin_of_the_students_school()
    {
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(otherSchool);
        using var coach = CoachClient(Ayse);
        var url = $"/api/v1/students/{Ali}/erasure";

        var missing = await Admin.PostAsJsonAsync(url, new { reason = (string?)null }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(missing)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("Field.Required");
        var unknown = await Admin.PostAsJsonAsync(url, new { reason = "Because" }, Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(unknown)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("Field.Invalid");
        (await coach.PostAsJsonAsync(url, new { reason = "DataSubjectRequest" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var other = await otherAdmin.PostAsJsonAsync(url, new { reason = "DataSubjectRequest" }, Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{Ali}", Ct)).GetProperty("firstName").GetString().ShouldBe("Ali");
        (await AuditRowsAsync()).ShouldNotContain(r => r.Action == "Erase");
    }
}

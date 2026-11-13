using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using SporAkademi.IntegrationTests.Attendance;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.Testing;

namespace SporAkademi.IntegrationTests.Audit;

/// <summary>
/// The attendance fixture (school, coach Ayşe, students Ali and Veli in a scheduled group) plus helpers for the personal
/// data audit log (P4-05) and the data subject requests (P4-06).
/// </summary>
public abstract class PersonalDataTest(DatabaseFixture database) : AttendanceTest(database)
{
    /// <summary>One row of <c>audit.audit_entries</c>, read with the migrator (every school).</summary>
    public sealed record AuditRow(
        Guid TenantId,
        string Action,
        string EntityType,
        Guid? EntityId,
        string[] Fields,
        Guid? UserId,
        string? UserRole,
        string? CorrelationId,
        string? Reason);

    protected async Task<List<AuditRow>> AuditRowsAsync()
    {
        var rows = await RowsAsync(
            "SELECT tenant_id, action, entity_type, entity_id, fields, user_id, user_role, correlation_id, reason FROM audit.audit_entries");
        return [.. rows.Select(r => new AuditRow(
            (Guid)r[0]!,
            (string)r[1]!,
            (string)r[2]!,
            (Guid?)r[3],
            (string[])r[4]!,
            (Guid?)r[5],
            (string?)r[6],
            (string?)r[7],
            (string?)r[8]))];
    }

    /// <summary>Every stored audit row as text, to prove that no value of the audited records was written.</summary>
    protected async Task<string> AuditTableTextAsync() =>
        (string?)(await RowsAsync("SELECT coalesce(string_agg(a::text, E'\\n'), '') FROM audit.audit_entries a")).Single()[0] ?? string.Empty;

    protected async Task<JsonElement> AuditLogAsync(string query = "", HttpClient? client = null)
    {
        var response = await (client ?? Admin).GetAsync(new Uri($"/api/v1/audit-log{query}", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    protected async Task<HttpResponseMessage> UploadAsync(Guid studentId, string type, byte[] content, string fileName, HttpClient? client = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(type), "type");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await (client ?? Admin).PostAsync(new Uri($"/api/v1/students/{studentId}/documents", UriKind.Relative), form, Ct);
    }

    protected async Task<Guid> UploadHealthReportAsync(Guid studentId)
    {
        var response = await UploadAsync(studentId, "HealthReport", TestFiles.Pdf(), "Ali Yılmaz rapor.pdf");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return IdOf(await BodyOf(response));
    }

    /// <summary>A fee from January and one cash payment for the student's (only) enrollment.</summary>
    protected async Task<Guid> PayJanuaryAsync(Guid studentId)
    {
        await DefineJanuaryFeeAsync();
        return await PayCashAsync(studentId);
    }

    protected async Task DefineJanuaryFeeAsync() =>
        (await Admin.PostAsJsonAsync($"/api/v1/groups/{GroupId}/fees", new { effectiveFrom = "2027-01", amount = new { amount = "1000.00", currency = "TRY" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

    protected async Task<Guid> EnrollmentOfAsync(Guid studentId) =>
        (await StudentEnrollmentsAsync(studentId)).EnumerateArray().Single().GetProperty("id").GetGuid();

    /// <summary>P2-F1: an online payment attempt of January (Fake provider), not completed yet, and its sandbox token.</summary>
    protected async Task<(Guid Id, string Token)> StartOnlineAsync(Guid studentId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/online-payments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enrollmentId = await EnrollmentOfAsync(studentId), period = "2027-01" }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await Admin.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var started = await BodyOf(response);
        var url = started.GetProperty("checkoutUrl").GetString()!;
        return (IdOf(started), url[(url.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);
    }

    /// <summary>The payer pays on the Fake sandbox page; returns the recorded payment.</summary>
    protected async Task<Guid> CompleteOnlineAsync(Guid onlinePayment, string token)
    {
        using (var payer = Api.CreateClient())
        {
            (await payer.PostAsJsonAsync("/api/v1/sandbox-checkout/complete", new { token, succeed = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var paid = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/online-payments/{onlinePayment}", Ct);
        paid.GetProperty("status").GetString().ShouldBe("Succeeded");
        return paid.GetProperty("paymentId").GetGuid();
    }

    protected async Task<Guid> PayCashAsync(Guid studentId)
    {
        var enrollment = await EnrollmentOfAsync(studentId);
        var payment = await Admin.PostAsJsonAsync(
            "/api/v1/payments",
            new { enrollmentId = enrollment, period = "2027-01", amount = new { amount = "1000.00", currency = "TRY" }, method = "Cash", paidOn = "2027-01-10" },
            Ct);
        payment.StatusCode.ShouldBe(HttpStatusCode.Created);
        return IdOf(await BodyOf(payment));
    }

    protected HttpClient ParentClient()
    {
        var client = Api.CreateClient();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "Parent", School.Value, Api.Clock.GetUtcNow()));
        return client;
    }

    protected async Task<long> CountWithAppRoleAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }
}

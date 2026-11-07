using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Enrollments;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// The enrollments fixture plus fee and payment helpers. The test clock starts on 2027-01-14 (Istanbul), so the
/// school's current month is 2027-01; tests that need later months move the clock and take a fresh admin client
/// (test tokens live 15 minutes).
/// </summary>
public abstract class BillingTest(DatabaseFixture database) : EnrollmentsTest(database)
{
    protected static object Money(string amount, string currency = "TRY") => new { amount, currency };

    protected Task<HttpResponseMessage> DefineFeeAsync(Guid groupId, string effectiveFrom, string amount, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/v1/groups/{groupId}/fees", new { effectiveFrom, amount = Money(amount) }, Ct);

    protected async Task DefineFeeOkAsync(Guid groupId, string effectiveFrom, string amount, HttpClient? client = null) =>
        (await DefineFeeAsync(groupId, effectiveFrom, amount, client)).StatusCode.ShouldBe(HttpStatusCode.Created);

    protected Task<HttpResponseMessage> RecordAsync(
        Guid enrollmentId,
        string period,
        string amount,
        string method = "Cash",
        string? paidOn = "2027-01-10",
        HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync("/api/v1/payments", new { enrollmentId, period, amount = Money(amount), method, paidOn }, Ct);

    protected async Task<JsonElement> RecordOkAsync(Guid enrollmentId, string period, string amount, HttpClient? client = null, string? paidOn = "2027-01-10")
    {
        var response = await RecordAsync(enrollmentId, period, amount, paidOn: paidOn, client: client);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await BodyOf(response);
    }

    protected Task<HttpResponseMessage> CancelAsync(Guid paymentId, string? reason, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/v1/payments/{paymentId}/cancel", new { reason }, Ct);

    protected async Task<JsonElement> OutstandingAsync(string query = "", HttpClient? client = null)
    {
        var response = await (client ?? Admin).GetAsync(new Uri($"/api/v1/dues/outstanding{query}", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    /// <summary>A student enrolled in a new group (with a fee from the current month unless <paramref name="fee"/> is null).</summary>
    protected async Task<(Guid Student, Guid Group, Guid Enrollment)> EnrolledAsync(
        string groupName = "U10 Futbol",
        string? fee = "1000.00",
        string startDate = "2027-01-04",
        string firstName = "Ali")
    {
        var group = IdOf(await GroupAsync(groupName));
        if (fee is not null)
        {
            await DefineFeeOkAsync(group, "2027-01", fee);
        }

        var student = await RegisterStudentAsync(firstName);
        var enrollment = IdOf(await EnrollOkAsync(student, group, startDate));
        return (student, group, enrollment);
    }

    /// <summary>Moves the school's clock and returns an admin client with a token minted at the new time.</summary>
    protected HttpClient AdvanceTo(DateTimeOffset utc)
    {
        Api.Clock.SetUtcNow(utc);
        return Api.SchoolAdminClient(School);
    }

    protected static string AmountOf(JsonElement money) => money.GetProperty("amount").GetString()!;

    /// <summary>P2-F1: starts an online payment of a due with an <c>Idempotency-Key</c> (none when <paramref name="key"/> is null).</summary>
    protected async Task<HttpResponseMessage> StartOnlineAsync(Guid enrollmentId, string period, string? key, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/online-payments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enrollmentId, period }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await (client ?? Admin).SendAsync(request, Ct);
    }

    protected async Task<JsonElement> StartOnlineOkAsync(Guid enrollmentId, string period = "2027-01", string? key = null, HttpClient? client = null)
    {
        var response = await StartOnlineAsync(enrollmentId, period, key ?? $"key-{Guid.NewGuid():N}", client);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await BodyOf(response);
    }
}

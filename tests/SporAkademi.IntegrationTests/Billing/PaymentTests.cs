using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

[Trait("Story", "US-23")]
public sealed class PaymentTests(DatabaseFixture database) : BillingTest(database)
{
    [Fact]
    public async Task A_recorded_payment_is_stored_and_lowers_that_months_due()
    {
        var (student, group, enrollment) = await EnrolledAsync(fee: "1000.00");

        var response = await RecordAsync(enrollment, "2027-01", "400", method: "BankTransfer", paidOn: "2027-01-12");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var payment = await BodyOf(response);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/payments/{IdOf(payment)}");
        payment.GetProperty("enrollmentId").GetGuid().ShouldBe(enrollment);
        payment.GetProperty("studentId").GetGuid().ShouldBe(student);
        payment.GetProperty("studentFirstName").GetString().ShouldBe("Ali");
        payment.GetProperty("groupId").GetGuid().ShouldBe(group);
        payment.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        payment.GetProperty("period").GetString().ShouldBe("2027-01");
        payment.GetProperty("amount").GetProperty("amount").GetString().ShouldBe("400.00");
        payment.GetProperty("method").GetString().ShouldBe("BankTransfer");
        payment.GetProperty("paidOn").GetString().ShouldBe("2027-01-12");
        payment.GetProperty("status").GetString().ShouldBe("Recorded");

        var due = (await OutstandingAsync()).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        AmountOf(due.GetProperty("paid")).ShouldBe("400.00");
        AmountOf(due.GetProperty("remaining")).ShouldBe("600.00");
    }

    [Fact]
    public async Task Money_is_stored_as_decimal_never_as_float()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: "0.30");
        await RecordOkAsync(enrollment, "2027-01", "0.10");
        await RecordOkAsync(enrollment, "2027-01", "0.20");

        var columns = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"""
                SELECT table_name || '.' || column_name || ':' || data_type || '(' || numeric_precision || ',' || numeric_scale || ')' AS "Value"
                FROM information_schema.columns
                WHERE column_name = 'amount' AND table_schema IN ('billing', 'groups')
                ORDER BY table_name
                """)
            .ToListAsync(Ct));

        columns.ShouldBe(["dues_reminders.amount:numeric(12,2)", "group_fees.amount:numeric(12,2)", "online_payments.amount:numeric(12,2)", "payment_refunds.amount:numeric(12,2)", "payments.amount:numeric(12,2)"]);
        // 0.10 + 0.20 is exactly the 0.30 fee: nothing is left owing.
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Theory]
    [InlineData("0", "amount", "Payment.InvalidAmount")]
    [InlineData("0.00", "amount", "Payment.InvalidAmount")]
    [InlineData("1.001", "amount", "Money.Invalid")]
    public async Task Amount_must_be_positive_money(string amount, string field, string code)
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var response = await RecordAsync(enrollment, "2027-01", amount);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task The_enrollment_must_have_been_active_in_the_period()
    {
        var (_, _, enrollment) = await EnrolledAsync(startDate: "2027-01-04");

        var response = await RecordAsync(enrollment, "2026-12", "100");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Payment.EnrollmentNotActiveInPeriod");
    }

    [Fact]
    public async Task The_group_must_have_a_fee_for_the_period()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: null);

        var response = await RecordAsync(enrollment, "2027-01", "100");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Payment.FeeNotDefined");
    }

    [Theory]
    [InlineData("Cheque", "2027-01-10", "method")]
    [InlineData("Cash", "2027-01-15", "paidOn")]
    [InlineData("Cash", null, "paidOn")]
    public async Task Method_and_a_payment_date_up_to_today_are_required(string method, string? paidOn, string field)
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var response = await RecordAsync(enrollment, "2027-01", "100", method, paidOn);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        if (field != "method")
        {
            (await BodyOf(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_wrong_entry_is_cancelled_with_a_reason_and_stays_visible()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: "1000.00");
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "1000"));
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);

        var response = await CancelAsync(payment, "  Yanlış öğrenciye girildi  ");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancelled = await BodyOf(response);
        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        cancelled.GetProperty("cancelReason").GetString().ShouldBe("Yanlış öğrenciye girildi");
        cancelled.GetProperty("cancelledBy").GetGuid().ShouldNotBe(Guid.Empty);
        cancelled.GetProperty("cancelledAt").GetDateTimeOffset().ShouldBe(Api.Clock.GetUtcNow());
        AmountOf(cancelled.GetProperty("amount")).ShouldBe("1000.00");

        // The cancelled payment no longer counts: the month is owed again.
        AmountOf((await OutstandingAsync()).GetProperty("items")[0].GetProperty("remaining")).ShouldBe("1000.00");
        var again = await CancelAsync(payment, "tekrar");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("Payment.AlreadyCancelled");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Cancelling_requires_a_reason(string? reason)
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));

        var response = await CancelAsync(payment, reason);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("Payment.CancelReasonRequired");
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment}", Ct)).GetProperty("status").GetString().ShouldBe("Recorded");
    }

    [Fact]
    public async Task Payments_can_never_be_edited_or_deleted()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));

        var put = await Admin.PutAsJsonAsync($"/api/v1/payments/{payment}", new { amount = Money("1.00") }, Ct);
        var delete = await Admin.DeleteAsync(new Uri($"/api/v1/payments/{payment}", UriKind.Relative), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        delete.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        AmountOf((await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment}", Ct)).GetProperty("amount")).ShouldBe("100.00");
    }

    [Fact]
    public async Task Concurrent_cancels_cancel_once()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => CancelAsync(payment, $"gerekçe {i}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.OK)
            .ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.UnprocessableEntity);
        var winner = await BodyOf(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment}", Ct)).GetProperty("cancelReason").GetString()
            .ShouldBe(winner.GetProperty("cancelReason").GetString());
    }

    [Fact]
    public async Task Payments_are_listed_newest_first_and_filtered()
    {
        var (aliStudent, u10, aliEnrollment) = await EnrolledAsync("U10 Futbol", firstName: "Ali");
        var (_, u12, veliEnrollment) = await EnrolledAsync("U12 Futbol", firstName: "Veli");
        await DefineFeeOkAsync(u10, "2027-02", "1000.00");
        var first = IdOf(await RecordOkAsync(aliEnrollment, "2027-01", "100", paidOn: "2027-01-05"));
        var second = IdOf(await RecordOkAsync(aliEnrollment, "2027-02", "200", paidOn: "2027-01-12"));
        var third = IdOf(await RecordOkAsync(veliEnrollment, "2027-01", "300", paidOn: "2027-01-08"));
        await CancelAsync(first, "yanlış");

        async Task<List<Guid>> Ids(string query) =>
            [.. (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/payments{query}", Ct)).GetProperty("items").EnumerateArray().Select(IdOf)];

        (await Ids("")).ShouldBe([second, third, first]);
        (await Ids($"?studentId={aliStudent}")).ShouldBe([second, first]);
        (await Ids($"?groupId={u12}")).ShouldBe([third]);
        (await Ids("?period=2027-01")).ShouldBe([third, first]);
        (await Ids("?status=Cancelled")).ShouldBe([first]);
        (await Ids($"?groupId={u10}&period=2027-01&status=Recorded")).ShouldBeEmpty();
        (await Admin.GetAsync(new Uri("/api/v1/payments?period=2027-1", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Another_schools_payments_and_enrollments_are_not_found()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));
        using var other = Api.SchoolAdminClient(await TestSchools.CreateAsync(Api, "okul-b"));

        (await other.GetAsync(new Uri($"/api/v1/payments/{payment}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var cancel = await CancelAsync(payment, "deneme", other);
        cancel.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(cancel)).ShouldBe("Payment.NotFound");
        var record = await RecordAsync(enrollment, "2027-01", "100", client: other);
        record.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(record)).ShouldBe("Enrollment.NotFound");
        (await other.GetFromJsonAsync<JsonElement>("/api/v1/payments", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await OutstandingAsync(client: other)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Coaches_have_no_access_to_payments()
    {
        using var coach = CoachClient(Ayse);

        (await coach.GetAsync(new Uri("/api/v1/payments", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.GetAsync(new Uri("/api/v1/dues/outstanding", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

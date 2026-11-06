using System.Net;
using System.Text.Json;
using Npgsql;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// Audit guarantees that hold below the application (DEC-185, DEC-186): run as the app role, raw SQL can neither
/// edit nor delete a payment, and a payment's student and group always match its enrollment.
/// </summary>
[Trait("Story", "US-23")]
public sealed class PaymentIntegrityTests(DatabaseFixture database) : BillingTest(database)
{
    private async Task<int> ExecuteAsAppAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(Ct);
    }

    [Theory]
    [InlineData("UPDATE billing.payments SET amount = 1 WHERE id = @id")]
    [InlineData("UPDATE billing.payments SET period = '2026-12' WHERE id = @id")]
    [InlineData("UPDATE billing.payments SET paid_on = '2027-01-01' WHERE id = @id")]
    [InlineData("UPDATE billing.payments SET method = 'BankTransfer' WHERE id = @id")]
    [InlineData("DELETE FROM billing.payments WHERE id = @id")]
    public async Task The_app_role_cannot_edit_or_delete_a_payment(string sql)
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));

        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsAppAsync(sql, ("id", payment)));

        error.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        var stored = await BodyOf(await Admin.GetAsync(new Uri($"/api/v1/payments/{payment}", UriKind.Relative), Ct));
        AmountOf(stored.GetProperty("amount")).ShouldBe("100.00");
        stored.GetProperty("period").GetString().ShouldBe("2027-01");
    }

    [Fact]
    public async Task A_cancelled_payment_cannot_be_reinstated_or_cancelled_again()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100"));
        (await CancelAsync(payment, "yanlış")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var reinstate = await Should.ThrowAsync<PostgresException>(() => ExecuteAsAppAsync(
            "UPDATE billing.payments SET status = 'Recorded', cancel_reason = NULL, cancelled_at = NULL, cancelled_by = NULL WHERE id = @id",
            ("id", payment)));
        var rewrite = await Should.ThrowAsync<PostgresException>(() => ExecuteAsAppAsync(
            "UPDATE billing.payments SET cancel_reason = 'başka bir gerekçe' WHERE id = @id", ("id", payment)));

        reinstate.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
        rewrite.SqlState.ShouldBe(PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task A_payment_cannot_name_another_student_or_group_than_its_enrollment()
    {
        var (_, _, enrollment) = await EnrolledAsync("U10 Futbol", firstName: "Ali");
        var (otherStudent, otherGroup, _) = await EnrolledAsync("U12 Futbol", firstName: "Veli");

        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsAppAsync(
            """
            INSERT INTO billing.payments (id, tenant_id, enrollment_id, student_id, group_id, period, amount, currency, method, paid_on, status, created_at, updated_at)
            VALUES (@id, @tenant, @enrollment, @student, @group, '2027-01', 10, 'TRY', 'Cash', '2027-01-10', 'Recorded', now(), now())
            """,
            ("id", Guid.CreateVersion7()),
            ("tenant", School.Value),
            ("enrollment", enrollment),
            ("student", otherStudent),
            ("group", otherGroup)));

        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_periods_total_stays_within_the_money_bound()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: "1000.00");
        await RecordOkAsync(enrollment, "2027-01", "9999999999.99");

        var response = await RecordAsync(enrollment, "2027-01", "0.01");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("amount")[0].GetString().ShouldBe("Money.Invalid");
        // The overpaid month is simply settled; the report does not fail.
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Unicode_digits_are_a_validation_error_not_a_crash()
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var response = await RecordAsync(enrollment, "2027-01", "١٢٣");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("amount")[0].GetString().ShouldBe("Money.Invalid");
    }

    [Fact]
    public async Task Without_a_period_the_list_looks_back_24_months()
    {
        await EnrolledAsync(fee: "100.00", startDate: "2027-01-04");
        var later = AdvanceTo(new DateTimeOffset(2029, 2, 10, 9, 0, 0, TimeSpan.Zero));

        var page = await OutstandingAsync("?pageSize=100", later);

        page.GetProperty("totalCount").GetInt32().ShouldBe(24);
        var periods = page.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("period").GetString()).ToList();
        periods[0].ShouldBe("2027-03");
        periods[^1].ShouldBe("2029-02");
        // An older month is still reachable with an explicit period.
        (await OutstandingAsync("?period=2027-01", later)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }
}

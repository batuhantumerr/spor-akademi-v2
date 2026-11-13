using System.Net;
using System.Text.Json;
using SporAkademi.Infrastructure.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P2-F1 exit criterion: a failed or duplicated webhook is retried without double charging (DEC-342, DEC-343, DEC-344).
/// The Fake provider signs its webhooks exactly like iyzico's hosted checkout (<c>X-IYZ-SIGNATURE-V3</c>).
/// </summary>
[Trait("Story", "US-23")]
[Trait("Roadmap", "P2-F1")]
public sealed class OnlinePaymentWebhookTests(DatabaseFixture database) : OnlinePaymentTest(database)
{
    /// <summary>A started attempt whose payer paid on the sandbox page, but whose results were not applied yet.</summary>
    private async Task<(Guid Id, string Token)> PaidButNotAppliedAsync()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        var token = TokenOf(started);
        // The sandbox endpoint also delivers the webhook; complete in the provider only, so the test delivers it.
        Fake.Complete(token, succeed: true).ShouldBeTrue();
        return (IdOf(started), token);
    }

    [Fact]
    public async Task A_signed_webhook_records_the_payment_and_a_duplicate_is_acknowledged_without_a_second_credit()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);

        var first = await WebhookAsync(body, headers);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(first)).GetProperty("outcome").GetString().ShouldBe("Applied");

        var duplicate = await WebhookAsync(body, headers);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(duplicate)).GetProperty("outcome").GetString().ShouldBe("Unchanged");

        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Succeeded");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(1);
    }

    [Fact]
    public async Task A_webhook_that_fails_is_retried_by_the_provider_and_credited_once()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);
        Fake.FailNextCalls(1);

        var failed = await WebhookAsync(body, headers);
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(failed)).ShouldBe("OnlinePayment.ProviderUnavailable");
        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Pending");
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(0);

        var retried = await WebhookAsync(body, headers);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await WebhookAsync(body, headers);
        (await BodyOf(again)).GetProperty("outcome").GetString().ShouldBe("Unchanged");

        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// Phase 2 exit criterion: the database fails while the payment is being written, so the delivery answers 500 and
    /// leaves nothing behind (no notification, no payment, the attempt still pending). The provider's retry credits the
    /// due once and a further duplicate changes nothing.
    /// </summary>
    [Fact]
    public async Task A_webhook_that_fails_while_writing_the_payment_is_rolled_back_and_its_retry_credits_once()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);

        await ExecuteAsMigratorAsync("""
            CREATE FUNCTION billing.test_fail_payment_insert() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'simulated database failure'; END $$;
            CREATE TRIGGER test_fail_payment_insert BEFORE INSERT ON billing.payments
            FOR EACH ROW EXECUTE FUNCTION billing.test_fail_payment_insert();
            """);
        HttpResponseMessage failed;
        try
        {
            failed = await WebhookAsync(body, headers);
        }
        finally
        {
            await ExecuteAsMigratorAsync("""
                DROP TRIGGER test_fail_payment_insert ON billing.payments;
                DROP FUNCTION billing.test_fail_payment_insert();
                """);
        }

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Pending");
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM billing.payments")).ShouldBe(0);

        var retried = await WebhookAsync(body, headers);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(retried)).GetProperty("outcome").GetString().ShouldBe("Applied");
        var duplicate = await WebhookAsync(body, headers);
        (await BodyOf(duplicate)).GetProperty("outcome").GetString().ShouldBe("Unchanged");

        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Succeeded");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(1);
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_webhooks_and_browser_returns_credit_the_due_exactly_once()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);

        var deliveries = Enumerable.Range(0, 12)
            .Select(i => i % 2 == 0 ? WebhookAsync(body, headers) : ReturnAsync(token))
            .ToList();
        var responses = await Task.WhenAll(deliveries);

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.SeeOther);
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(1);
        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Succeeded");
        var paid = await OutstandingAsync();
        paid.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_starts_with_the_same_key_create_one_attempt()
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => StartOnlineAsync(enrollment, "2027-01", "ayni-anahtar-123")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        // DEC-341: every loser is a replay of the winner, never "in progress".
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            loser.StatusCode.ShouldBe(HttpStatusCode.OK, await loser.Content.ReadAsStringAsync(Ct));
            loser.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        }

        var ids = new HashSet<Guid>();
        foreach (var response in responses.Where(r => r.IsSuccessStatusCode))
        {
            ids.Add(IdOf(await BodyOf(response)));
        }

        ids.Count.ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.online_payments")).ShouldBe(1);
    }

    [Fact]
    public async Task A_webhook_received_while_the_provider_still_reports_pending_is_applied_when_delivered_again()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        var token = TokenOf(started);
        var webhook = new HostedWebhook(FakePaymentProvider.EventType, "fp-early", token, IdOf(started).ToString(), "SUCCESS");
        var headers = new Dictionary<string, string>
        {
            [IyzicoProtocol.SignatureHeader] = IyzicoProtocol.WebhookSignature(new FakeProviderOptions().SigningKey, webhook),
        };

        // The provider has not settled the payment yet: nothing is applied and the delivery is not consumed.
        var early = await WebhookAsync(webhook.ToJson(), headers);
        (await BodyOf(early)).GetProperty("outcome").GetString().ShouldBe("Pending");
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(0);

        Fake.Complete(token, succeed: true).ShouldBeTrue();
        var retried = await WebhookAsync(webhook.ToJson(), headers);

        (await BodyOf(retried)).GetProperty("outcome").GetString().ShouldBe("Applied");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await BodyOf(await WebhookAsync(webhook.ToJson(), headers))).GetProperty("outcome").GetString().ShouldBe("Unchanged");
    }

    [Fact]
    public async Task A_charge_whose_amount_differs_from_the_attempt_is_rejected_and_not_credited()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        Fake.ReportPaidAmount(token, SporAkademi.Domain.SharedKernel.Money.Try(999m)).ShouldBeTrue();
        var (body, headers) = FakeWebhook(token);

        var response = await WebhookAsync(body, headers);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("OnlinePayment.ProviderMismatch");
        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Pending");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM billing.payment_notifications")).ShouldBe(0);
    }

    [Fact]
    public async Task A_webhook_body_over_16_kb_is_refused_before_it_is_read()
    {
        var body = "{\"token\":\"" + new string('a', 20_000) + "\"}";

        var response = await WebhookAsync(body, new Dictionary<string, string> { [IyzicoProtocol.SignatureHeader] = "00" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("Request.TooLarge");
    }

    [Fact]
    public async Task Concurrent_starts_with_different_keys_open_one_attempt_per_due()
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => StartOnlineAsync(enrollment, "2027-01", $"farkli-anahtar-{i}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        (await CountAsync("SELECT count(*) FROM billing.online_payments")).ShouldBe(1);
    }

    [Fact]
    public async Task A_webhook_with_a_wrong_or_missing_signature_changes_nothing()
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);
        var forged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [IyzicoProtocol.SignatureHeader] = new string('0', 64),
        };
        var tampered = body.Replace("SUCCESS", "FAILURE", StringComparison.Ordinal);

        (await WebhookAsync(body, forged)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await WebhookAsync(body, new Dictionary<string, string>())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var changed = await WebhookAsync(tampered, headers);
        changed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(changed)).ShouldBe("OnlinePayment.InvalidSignature");

        (await OnlinePaymentAsync(id)).GetProperty("status").GetString().ShouldBe("Pending");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_signed_webhook_for_an_unknown_checkout_is_not_found()
    {
        var webhook = new HostedWebhook(FakePaymentProvider.EventType, "fp-1", "fake-unknown", Guid.NewGuid().ToString(), "SUCCESS");
        var headers = new Dictionary<string, string>
        {
            [IyzicoProtocol.SignatureHeader] = IyzicoProtocol.WebhookSignature(new FakeProviderOptions().SigningKey, webhook),
        };

        var response = await WebhookAsync(webhook.ToJson(), headers);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_webhook_acts_for_the_school_of_the_stored_attempt_only()
    {
        // The webhook carries no school; the payment is recorded in the school that started the attempt.
        var (id, token) = await PaidButNotAppliedAsync();
        var otherSchool = await TestSchools.CreateAsync(Api, "baska-okul");
        var (body, headers) = FakeWebhook(token);

        (await WebhookAsync(body, headers)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountAsync($"SELECT count(*) FROM billing.payments WHERE method = 'Online' AND tenant_id = '{School.Value}'")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM billing.payments WHERE tenant_id = '{otherSchool.Value}'")).ShouldBe(0);
        using var other = Api.SchoolAdminClient(otherSchool);
        (await other.GetAsync(new Uri($"/api/v1/online-payments/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_browser_return_cannot_claim_a_result_the_provider_did_not_report()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);

        // Not paid on the provider's side: whatever the browser sends, nothing is credited.
        var returned = await ReturnAsync(TokenOf(started));

        returned.Headers.Location!.OriginalString.ShouldBe("/odeme-sonucu?durum=Pending");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
        (await ReturnAsync("fake-does-not-exist")).Headers.Location!.OriginalString.ShouldBe("/odeme-sonucu?durum=Unknown");
    }

    [Theory]
    [InlineData("UPDATE billing.online_payments SET status = 'Failed' WHERE id = @id")]
    [InlineData("UPDATE billing.online_payments SET payment_id = NULL, status = 'Pending' WHERE id = @id")]
    [InlineData("UPDATE billing.online_payments SET amount = 1 WHERE id = @id")]
    [InlineData("DELETE FROM billing.online_payments WHERE id = @id")]
    [InlineData("UPDATE billing.payments SET status = 'Cancelled', cancel_reason = 'x', cancelled_at = now(), cancelled_by = tenant_id WHERE online_payment_id = @id")]
    public async Task The_database_keeps_a_succeeded_online_payment_and_its_credit_as_they_are(string sql)
    {
        var (id, token) = await PaidButNotAppliedAsync();
        var (body, headers) = FakeWebhook(token);
        (await WebhookAsync(body, headers)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The migrator with the maintenance switch sees every row (row level security, P4-04): the triggers still refuse.
        await using var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.RestrictViolation);
        var stored = await OnlinePaymentAsync(id);
        stored.GetProperty("status").GetString().ShouldBe("Succeeded");
        stored.GetProperty("paymentId").ValueKind.ShouldBe(JsonValueKind.String);
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
    }
}

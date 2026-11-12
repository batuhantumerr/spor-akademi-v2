using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P3-13: refunding an online payment through the Fake sandbox provider, end to end over HTTP (DEC-416 – DEC-421): the
/// state machine, idempotency, races, provider refusals and unknown outcomes, and what the refund does to the due.
/// </summary>
[Trait("Story", "US-23")]
[Trait("Roadmap", "P3-13")]
public sealed class RefundTests(DatabaseFixture database) : OnlinePaymentTest(database)
{
    private const string Reason = "Kayıt erken bitti";

    private Task<int> RunReconcileJobAsync() =>
        InScopeAsync(services => services.GetRequiredService<OnlinePaymentReconcileJob>().RunAsync(Ct));

    private async Task<JsonElement> PaymentAsync(Guid id, HttpClient? client = null) =>
        await (client ?? Admin).GetFromJsonAsync<JsonElement>($"/api/v1/payments/{id}", Ct);

    private Task<long> RefundRowsAsync(string status) =>
        CountAsync($"SELECT count(*) FROM billing.payment_refunds WHERE status = '{status}'");

    [Fact]
    public async Task A_refund_returns_the_money_and_reopens_the_due()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (online, payment) = await PayOnlineAsync(enrollment);
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);

        var response = await RefundAsync(payment, "  " + Reason + "  ", "iade-anahtari-1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refunded = await BodyOf(response);
        refunded.GetProperty("status").GetString().ShouldBe("Refunded");
        refunded.GetProperty("onlinePaymentId").GetGuid().ShouldBe(online);
        var refund = refunded.GetProperty("refund");
        refund.GetProperty("status").GetString().ShouldBe("Succeeded");
        refund.GetProperty("reason").GetString().ShouldBe(Reason);
        refund.GetProperty("attempts").GetInt32().ShouldBe(1);
        refund.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await OnlinePaymentAsync(online)).GetProperty("refundStatus").GetString().ShouldBe("Succeeded");

        // DEC-420: the refunded payment no longer counts; the due is owed again and can be paid again online.
        var due = (await OutstandingAsync()).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        AmountOf(due.GetProperty("remaining")).ShouldBe("1000.00");
        AmountOf(due.GetProperty("paid")).ShouldBe("0.00");
        (await StartOnlineAsync(enrollment, "2027-01", "yeniden-ode-1")).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The payment stays visible as refunded; the status filter finds it; one refund message is queued (ids only).
        var list = await BodyOf(await Admin.GetAsync(new Uri("/api/v1/payments?status=Refunded", UriKind.Relative), Ct));
        list.GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM outbox.messages WHERE type = 'billing.payment-refunded.v1'")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM outbox.messages WHERE type = 'billing.payment-refunded.v1' AND payload::text LIKE '%Kayıt%'")).ShouldBe(0);
    }

    [Fact]
    public async Task A_declined_refund_fails_keeps_the_payment_and_can_be_retried()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.DeclineNextRefunds(1);

        var declined = await RefundAsync(payment, Reason, "iade-red-1");

        declined.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(declined)).ShouldBe("Refund.ProviderDeclined");
        var kept = await PaymentAsync(payment);
        kept.GetProperty("status").GetString().ShouldBe("Recorded");
        kept.GetProperty("refund").GetProperty("status").GetString().ShouldBe("Failed");
        kept.GetProperty("refund").GetProperty("failureCode").GetString().ShouldBe("FakeRefundDeclined");
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // The replay of the declined request reports it as it stands; a new request (new key) is the retry.
        var replay = await RefundAsync(payment, Reason, "iade-red-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await BodyOf(replay)).GetProperty("refund").GetProperty("status").GetString().ShouldBe("Failed");

        (await RefundOkAsync(payment)).GetProperty("status").GetString().ShouldBe("Refunded");
        (await RefundRowsAsync("Failed")).ShouldBe(1);
        (await RefundRowsAsync("Succeeded")).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_outcome_stays_pending_and_the_reconciliation_job_finishes_it()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);

        var accepted = await RefundAsync(payment, Reason, "iade-belirsiz-1");

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var pending = await BodyOf(accepted);
        pending.GetProperty("status").GetString().ShouldBe("Recorded");
        pending.GetProperty("refund").GetProperty("status").GetString().ShouldBe("Pending");
        pending.GetProperty("refund").GetProperty("attempts").GetInt32().ShouldBe(1);
        // While pending the money may or may not be back: the due stays paid and a second request is refused.
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
        var second = await RefundAsync(payment, Reason, "iade-belirsiz-2");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(second)).ShouldBe("Refund.InProgress");
        var calls = Fake.RefundCalls;
        (await RefundAsync(payment, Reason, "iade-belirsiz-1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Fake.RefundCalls.ShouldBe(calls); // a replay never calls the provider

        (await RunReconcileJobAsync()).ShouldBe(0); // too recent
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunReconcileJobAsync()).ShouldBe(1);
        (await RunReconcileJobAsync()).ShouldBe(0);

        var refunded = await PaymentAsync(payment);
        refunded.GetProperty("status").GetString().ShouldBe("Refunded");
        refunded.GetProperty("refund").GetProperty("attempts").GetInt32().ShouldBe(2);
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_replays_and_another_body_is_refused()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        var (_, _, other) = await EnrolledAsync("U12 Futbol", firstName: "Veli");
        var (_, otherPayment) = await PayOnlineAsync(other);

        (await RefundAsync(payment, Reason, "tekrar-iade-1")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var calls = Fake.RefundCalls;
        var replay = await RefundAsync(payment, Reason, "tekrar-iade-1");

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await BodyOf(replay)).GetProperty("status").GetString().ShouldBe("Refunded");
        Fake.RefundCalls.ShouldBe(calls);
        var otherReason = await RefundAsync(payment, "Başka bir gerekçe", "tekrar-iade-1");
        otherReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(otherReason)).ShouldBe("Idempotency.KeyReused");
        var otherPaymentSameKey = await RefundAsync(otherPayment, Reason, "tekrar-iade-1");
        (await CodeOf(otherPaymentSameKey)).ShouldBe("Idempotency.KeyReused");
        (await CountAsync("SELECT count(*) FROM billing.payment_refunds")).ShouldBe(1);

        var again = await RefundAsync(payment, Reason, "tekrar-iade-2");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("Refund.AlreadyRefunded");
    }

    [Fact]
    public async Task Concurrent_refunds_of_one_payment_return_the_money_once()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (online, payment) = await PayOnlineAsync(enrollment);
        var calls = Fake.RefundCalls;

        // Two admins and a double click: six requests with different keys at once.
        var responses = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => RefundAsync(payment, Reason, $"yaris-iade-{i:D2}")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var loser in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            ((int)loser.StatusCode).ShouldBeOneOf(409, 422);
            (await CodeOf(loser)).ShouldBeOneOf("Refund.InProgress", "Refund.AlreadyRefunded");
        }

        Fake.RefundCalls.ShouldBe(calls + 1);
        (await CountAsync("SELECT count(*) FROM billing.payment_refunds")).ShouldBe(1);
        (await CountAsync($"SELECT count(*) FROM billing.payments WHERE status = 'Refunded' AND online_payment_id = '{online}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_refund_once_and_replay()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);

        var responses = await Task.WhenAll(Enumerable.Range(1, 6).Select(_ => RefundAsync(payment, Reason, "ayni-anahtar-iade")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        responses.Count(r => !r.Headers.Contains("Idempotent-Replayed")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.payment_refunds")).ShouldBe(1);
    }

    [Fact]
    public async Task Refunding_an_overpayment_keeps_the_due_paid()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        using var later = AdvanceTo(Api.Clock.GetUtcNow().AddMinutes(31));
        (await RecordAsync(enrollment, "2027-01", "1000.00", client: later)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CompleteSandboxAsync(TokenOf(started))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var overpaid = await OnlinePaymentAsync(IdOf(started), later);
        overpaid.GetProperty("overpaid").GetBoolean().ShouldBeTrue();

        await RefundOkAsync(overpaid.GetProperty("paymentId").GetGuid(), "Fazla ödeme iadesi", later);

        // DEC-420: the cash payment still covers January; nothing is owed.
        (await OutstandingAsync(client: later)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        var after = await OnlinePaymentAsync(IdOf(started), later);
        after.GetProperty("overpaid").GetBoolean().ShouldBeTrue();
        after.GetProperty("refundStatus").GetString().ShouldBe("Succeeded");
        (await CountAsync("SELECT coalesce(sum(amount), 0)::bigint FROM billing.payments WHERE status = 'Recorded'")).ShouldBe(1000);
    }

    [Fact]
    public async Task Refunding_a_closed_months_payment_keeps_the_frozen_fee_and_reopens_the_due()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        (await Admin.PostAsJsonAsync("/api/v1/settings/billing-rules", new
        {
            effectiveFrom = "2027-01",
            secondChildPercent = 10,
            thirdAndLaterChildPercent = 15,
            singleChargeOnGroupChange = true,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var (_, payment) = await PayOnlineAsync(enrollment);
        using var february = AdvanceTo(new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero));
        (await InScopeAsync(services => services.GetRequiredService<BillingPeriodCloseJob>().RunAsync(Ct))).ShouldBe(1);

        await RefundOkAsync(payment, "Ocak antrenmanları yapılmadı", february);

        // DEC-421: January's charge stays as it closed (1000.00); the refunded money no longer pays it.
        var january = (await OutstandingAsync("?period=2027-01", february)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        AmountOf(january.GetProperty("fee")).ShouldBe("1000.00");
        AmountOf(january.GetProperty("remaining")).ShouldBe("1000.00");
        (await CountAsync("SELECT count(*) FROM billing.charge_snapshots WHERE period = '2027-01'")).ShouldBe(1);
    }

    [Fact]
    public async Task Only_online_payments_are_refunded_and_the_request_is_validated()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var cash = IdOf(await RecordOkAsync(enrollment, "2027-01", "400.00"));

        var manual = await RefundAsync(cash, Reason, "nakit-iade-1");
        manual.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(manual)).ShouldBe("Refund.NotOnline");

        var noKey = await RefundAsync(cash, Reason, null);
        noKey.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(noKey)).GetProperty("errors").GetProperty("idempotencyKey")[0].GetString().ShouldBe("Idempotency.KeyRequired");

        var noReason = await RefundAsync(cash, "   ", "nakit-iade-2");
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(noReason)).GetProperty("errors").GetProperty("reason")[0].GetString().ShouldBe("Refund.ReasonRequired");

        var unknown = await RefundAsync(Guid.CreateVersion7(), Reason, "olmayan-iade-1");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(unknown)).ShouldBe("Payment.NotFound");
        (await CountAsync("SELECT count(*) FROM billing.payment_refunds")).ShouldBe(0);
    }

    [Fact]
    public async Task The_database_refuses_to_change_a_refund_or_refund_a_manual_payment()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        await RefundOkAsync(payment);
        var cash = IdOf(await RecordOkAsync(enrollment, "2027-01", "10.00"));

        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync("UPDATE billing.payment_refunds SET reason = 'değişti'"));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync("UPDATE billing.payment_refunds SET status = 'Failed', failure_code = 'x', provider_refund_id = NULL"));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync("DELETE FROM billing.payment_refunds"));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync($"UPDATE billing.payments SET status = 'Recorded' WHERE id = '{payment}'"));
        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync($"UPDATE billing.payments SET status = 'Refunded' WHERE id = '{cash}'"));
    }
}

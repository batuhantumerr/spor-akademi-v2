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

        // DEC-419: the replay of the declined request answers by its own status; a new request (new key) is the retry.
        var replay = await RefundAsync(payment, Reason, "iade-red-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        (await CodeOf(replay)).ShouldBe("Refund.ProviderDeclined");

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
        var pendingReplay = await RefundAsync(payment, Reason, "iade-belirsiz-1");
        pendingReplay.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await BodyOf(pendingReplay)).GetProperty("refund").GetProperty("status").GetString().ShouldBe("Pending");
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

        // A replay answers by the request's status at that moment: refunded (200) or still at the provider (202).
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Accepted);
        responses.Single(r => !r.Headers.Contains("Idempotent-Replayed")).StatusCode.ShouldBe(HttpStatusCode.OK);
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
        // DEC-420: the payment itself says, live from the due, by how much the due is over-covered.
        var onlinePayment = await PaymentAsync(overpaid.GetProperty("paymentId").GetGuid(), later);
        AmountOf(onlinePayment.GetProperty("overpaidBy")).ShouldBe("1000.00");

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
        // A request can only start pending and untried.
        await Should.ThrowAsync<Npgsql.PostgresException>(() => ExecuteAsMigratorAsync(
            "INSERT INTO billing.payment_refunds SELECT (r).* FROM (SELECT r FROM billing.payment_refunds r LIMIT 1) x"));
    }

    [Fact]
    public async Task A_refund_whose_answer_was_lost_is_found_refunded_by_the_payment_detail()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        var (calls, checks) = (Fake.RefundCalls, Fake.RefundChecks);
        Fake.LoseNextRefundAnswers(1);

        // The provider returned the money, but its answer never arrived.
        (await RefundAsync(payment, Reason, "kayip-cevap-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await PaymentAsync(payment)).GetProperty("refund").GetProperty("attempts").GetInt32().ShouldBe(1);

        // DEC-418: the re-send is refused as "already refunded"; the payment detail shows the money back.
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunReconcileJobAsync()).ShouldBe(1);

        var refunded = await PaymentAsync(payment);
        refunded.GetProperty("status").GetString().ShouldBe("Refunded");
        refunded.GetProperty("refund").GetProperty("status").GetString().ShouldBe("Succeeded");
        refunded.GetProperty("refund").GetProperty("attempts").GetInt32().ShouldBe(2);
        Fake.RefundCalls.ShouldBe(calls + 2);
        Fake.RefundChecks.ShouldBe(checks + 1);
    }

    [Fact]
    public async Task A_refused_retry_without_the_money_back_waits_for_review_and_the_admin_check_settles_it()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);
        (await RefundAsync(payment, Reason, "inceleme-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // The retry is refused and the detail shows nothing returned: not trusted as final, a person looks (DEC-418).
        Fake.DeclineNextRefunds(1);
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunReconcileJobAsync()).ShouldBe(0);
        var review = (await PaymentAsync(payment)).GetProperty("refund");
        review.GetProperty("status").GetString().ShouldBe("Pending");
        review.GetProperty("needsReview").GetBoolean().ShouldBeTrue();
        var calls = Fake.RefundCalls;
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunReconcileJobAsync()).ShouldBe(0);
        Fake.RefundCalls.ShouldBe(calls); // the job leaves it alone

        // DEC-418: "Sağlayıcıda kontrol et": the provider shows the money not returned, so the request fails.
        using var admin = Api.SchoolAdminClient(School);
        var checkedResponse = await admin.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        checkedResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(checkedResponse)).ShouldBe("Refund.ProviderDeclined");
        var failed = (await PaymentAsync(payment, admin)).GetProperty("refund");
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("NotRefundedAtProvider");
        (await RefundOkAsync(payment, client: admin)).GetProperty("status").GetString().ShouldBe("Refunded");
    }

    [Fact]
    public async Task The_admin_check_finds_a_lost_refund_after_a_quiet_minute()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.LoseNextRefundAnswers(1);
        (await RefundAsync(payment, Reason, "kontrol-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var tooSoon = await Admin.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        tooSoon.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(tooSoon)).ShouldBe("Refund.InProgress");

        using var later = AdvanceTo(Api.Clock.GetUtcNow().AddMinutes(2));
        var found = await later.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(found)).GetProperty("status").GetString().ShouldBe("Refunded");

        var nothingPending = await later.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        nothingPending.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(nothingPending)).ShouldBe("Refund.NotPending");
    }

    [Fact]
    public async Task Overlapping_reconciliation_runs_call_the_provider_once_per_refund()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);
        (await RefundAsync(payment, Reason, "cakisan-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        var calls = Fake.RefundCalls;

        // DEC-419: two runs (e.g. two API instances) reach the same pending refund; only one claims the attempt.
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => InScopeAsync(scope => scope.GetRequiredService<SporAkademi.Application.Billing.ITenantWorkScope>()
            .RunAsync(School.Value, services => services.GetRequiredService<SporAkademi.Application.Billing.PaymentRefundReconciliation>()
                .ReconcileAsync(TimeSpan.FromMinutes(10), Ct)))));

        runs.Sum().ShouldBe(1);
        Fake.RefundCalls.ShouldBe(calls + 1);
        (await PaymentAsync(payment)).GetProperty("status").GetString().ShouldBe("Refunded");
    }

    [Fact]
    public async Task Overlapping_reconciliation_jobs_run_once()
    {
        // DEC-419: the job holds a session advisory lock; a second run while it is held does nothing.
        await using var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using (var take = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(5999166636069703235)", connection))
        {
            await take.ExecuteNonQueryAsync(Ct);
        }

        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);
        (await RefundAsync(payment, Reason, "kilitli-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Api.Clock.Advance(TimeSpan.FromMinutes(11));

        (await RunReconcileJobAsync()).ShouldBe(0);
        (await PaymentAsync(payment)).GetProperty("status").GetString().ShouldBe("Recorded");

        await using (var release = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(5999166636069703235)", connection))
        {
            await release.ExecuteNonQueryAsync(Ct);
        }

        (await RunReconcileJobAsync()).ShouldBe(1);
    }

    private Task<HttpResponseMessage> ResolveAsync(Guid payment, bool refunded, string? note, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/v1/payments/{payment}/refund/resolve", new { refunded, note }, Ct);

    /// <summary>A refund the job gave up on after <c>MaxAutomaticAttempts</c> unknown outcomes; returns a fresh admin client.</summary>
    private async Task<HttpClient> StuckRefundAsync(Guid payment, string key)
    {
        Fake.FailNextCalls(3);
        (await RefundAsync(payment, Reason, key)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        for (var run = 0; run < 3; run++)
        {
            Api.Clock.Advance(TimeSpan.FromMinutes(11));
            (await RunReconcileJobAsync()).ShouldBe(0);
        }

        return Api.SchoolAdminClient(School);
    }

    [Fact]
    public async Task After_three_unanswered_attempts_the_job_stops_and_marks_the_refund_for_review()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);

        using var admin = await StuckRefundAsync(payment, "takili-1");

        var refund = (await PaymentAsync(payment, admin)).GetProperty("refund");
        refund.GetProperty("status").GetString().ShouldBe("Pending");
        refund.GetProperty("attempts").GetInt32().ShouldBe(3);
        refund.GetProperty("needsReview").GetBoolean().ShouldBeTrue();
        var calls = Fake.RefundCalls;
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunReconcileJobAsync()).ShouldBe(0);
        Fake.RefundCalls.ShouldBe(calls);
        (await PaymentAsync(payment, admin)).GetProperty("refund").GetProperty("attempts").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task A_refund_in_review_is_settled_by_hand_as_refunded_with_an_audited_note()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        using var admin = await StuckRefundAsync(payment, "elle-iade-1");

        var noNote = await ResolveAsync(payment, true, "  ", admin);
        noNote.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(noNote)).GetProperty("errors").GetProperty("note")[0].GetString().ShouldBe("Refund.ResolutionNoteRequired");

        var resolved = await ResolveAsync(payment, true, "Sağlayıcı panelinde iade görünüyor", admin);

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyOf(resolved);
        body.GetProperty("status").GetString().ShouldBe("Refunded");
        var refund = body.GetProperty("refund");
        refund.GetProperty("status").GetString().ShouldBe("Succeeded");
        refund.GetProperty("needsReview").GetBoolean().ShouldBeFalse();
        refund.GetProperty("resolutionNote").GetString().ShouldBe("Sağlayıcı panelinde iade görünüyor");
        refund.GetProperty("resolvedBy").GetGuid().ShouldNotBe(Guid.Empty);
        refund.GetProperty("resolvedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await CountAsync("SELECT count(*) FROM outbox.messages WHERE type = 'billing.payment-refunded.v1'")).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM outbox.messages WHERE payload::text LIKE '%panelinde%'")).ShouldBe(0);

        var again = await ResolveAsync(payment, false, "tekrar", admin);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("Refund.NotInReview");
    }

    [Fact]
    public async Task A_refund_in_review_settled_as_not_refunded_fails_and_can_be_requested_again()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        using var admin = await StuckRefundAsync(payment, "elle-red-1");

        var resolved = await ResolveAsync(payment, false, "Sağlayıcıda iade yok", admin);

        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyOf(resolved);
        body.GetProperty("status").GetString().ShouldBe("Recorded");
        body.GetProperty("refund").GetProperty("status").GetString().ShouldBe("Failed");
        body.GetProperty("refund").GetProperty("failureCode").GetString().ShouldBe("ResolvedNotRefunded");
        (await RefundOkAsync(payment, client: admin)).GetProperty("status").GetString().ShouldBe("Refunded");
    }

    [Fact]
    public async Task Only_a_refund_in_review_is_settled_by_hand()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);
        (await RefundAsync(payment, Reason, "bekleyen-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var pending = await ResolveAsync(payment, true, "erken");

        pending.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(pending)).ShouldBe("Refund.NotInReview");
        (await ResolveAsync(Guid.CreateVersion7(), true, "yok")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_admin_check_fails_a_refund_only_after_the_reconciliation_threshold()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (_, payment) = await PayOnlineAsync(enrollment);
        Fake.FailNextCalls(1);
        (await RefundAsync(payment, Reason, "esik-anahtari-1")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Past the quiet minute but younger than ReconcileAfter: "not refunded" may still change, so it stays pending.
        using var soon = AdvanceTo(Api.Clock.GetUtcNow().AddMinutes(2));
        var early = await soon.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        early.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await BodyOf(early)).GetProperty("refund").GetProperty("status").GetString().ShouldBe("Pending");

        using var later = AdvanceTo(Api.Clock.GetUtcNow().AddMinutes(11));
        var late = await later.PostAsync(new Uri($"/api/v1/payments/{payment}/refund/check", UriKind.Relative), null, Ct);
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PaymentAsync(payment, later)).GetProperty("refund").GetProperty("failureCode").GetString().ShouldBe("NotRefundedAtProvider");
    }

    [Fact]
    public async Task The_fake_provider_refunds_a_charge_once_like_iyzico()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var (online, _) = await PayOnlineAsync(enrollment);
        var charge = "fp-" + online.ToString("N")[..16];
        var request = new SporAkademi.Application.Billing.RefundRequest(Guid.CreateVersion7(), charge, SporAkademi.Domain.SharedKernel.Money.Try(1000m));

        (await Fake.RefundAsync(request, Ct)).Outcome.ShouldBe(SporAkademi.Application.Billing.ProviderRefundOutcome.Refunded);
        // DEC-418: a second refund of the payment, even with the same refund id, is refused as already refunded.
        var again = await Fake.RefundAsync(request, Ct);
        again.Outcome.ShouldBe(SporAkademi.Application.Billing.ProviderRefundOutcome.Verify);
        again.FailureCode.ShouldBe("FakeAlreadyRefunded");
        (await Fake.RefundAsync(request with { RefundId = Guid.CreateVersion7() }, Ct)).Outcome.ShouldBe(SporAkademi.Application.Billing.ProviderRefundOutcome.Verify);
        (await Fake.CheckRefundAsync(request, Ct)).Refunded.ShouldBeTrue();
    }
}

using System.Net;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>P2-F1: online dues payment through the Fake sandbox provider, end to end over HTTP.</summary>
[Trait("Story", "US-23")]
[Trait("Roadmap", "P2-F1")]
public sealed class OnlinePaymentTests(DatabaseFixture database) : OnlinePaymentTest(database)
{
    [Fact]
    public async Task A_due_is_paid_end_to_end_through_the_sandbox_provider()
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var started = await StartOnlineOkAsync(enrollment);
        started.GetProperty("status").GetString().ShouldBe("Pending");
        AmountOf(started.GetProperty("amount")).ShouldBe("1000.00");
        started.GetProperty("provider").GetString().ShouldBe("Fake");
        var token = TokenOf(started);
        started.GetProperty("checkoutUrl").GetString().ShouldBe($"/sandbox-odeme?token={token}");

        var completed = await CompleteSandboxAsync(token);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(completed)).GetProperty("callbackUrl").GetString().ShouldBe("/api/v1/payment-callbacks");

        var returned = await ReturnAsync(token);
        returned.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        returned.Headers.Location!.OriginalString.ShouldBe("/odeme-sonucu?durum=Succeeded");

        var paid = await OnlinePaymentAsync(IdOf(started));
        paid.GetProperty("status").GetString().ShouldBe("Succeeded");
        paid.GetProperty("checkoutUrl").ValueKind.ShouldBe(JsonValueKind.Null);
        var paymentId = paid.GetProperty("paymentId").GetGuid();
        var payment = await BodyOf(await Admin.GetAsync(new Uri($"/api/v1/payments/{paymentId}", UriKind.Relative), Ct));
        payment.GetProperty("method").GetString().ShouldBe("Online");
        AmountOf(payment.GetProperty("amount")).ShouldBe("1000.00");
        payment.GetProperty("period").GetString().ShouldBe("2027-01");
        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_amount_is_what_remains_of_the_due_and_nothing_due_is_refused()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        await RecordOkAsync(enrollment, "2027-01", "400.00");

        var started = await StartOnlineOkAsync(enrollment);
        AmountOf(started.GetProperty("amount")).ShouldBe("600.00");

        await CompleteSandboxAsync(TokenOf(started));
        var again = await StartOnlineAsync(enrollment, "2027-01", "another-key-1");
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("OnlinePayment.NothingDue");
    }

    [Fact]
    public async Task A_future_period_or_a_group_without_a_fee_cannot_be_paid()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var future = await StartOnlineAsync(enrollment, "2027-02", "future-key-1");
        future.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(future)).ShouldBe("OnlinePayment.PeriodInFuture");

        var (_, _, noFee) = await EnrolledAsync("U12 Futbol", fee: null, firstName: "Veli");
        var unpriced = await StartOnlineAsync(noFee, "2027-01", "no-fee-key-1");
        unpriced.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(unpriced)).ShouldBe("Payment.FeeNotDefined");
    }

    [Fact]
    public async Task The_same_idempotency_key_replays_the_same_attempt_and_another_body_is_refused()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var first = await StartOnlineAsync(enrollment, "2027-01", "tekrar-anahtari-1");
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        var replay = await StartOnlineAsync(enrollment, "2027-01", "tekrar-anahtari-1");
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        IdOf(await BodyOf(replay)).ShouldBe(IdOf(await BodyOf(first)));

        var reused = await StartOnlineAsync(enrollment, "2026-12", "tekrar-anahtari-1");
        reused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(reused)).ShouldBe("Idempotency.KeyReused");
        (await CountAsync("SELECT count(*) FROM billing.online_payments")).ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "Idempotency.KeyRequired")]
    [InlineData("short", "Idempotency.KeyInvalid")]
    [InlineData("has spaces in it", "Idempotency.KeyInvalid")]
    public async Task The_idempotency_key_is_required_and_must_be_a_token(string? key, string code)
    {
        var (_, _, enrollment) = await EnrolledAsync();

        var response = await StartOnlineAsync(enrollment, "2027-01", key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("idempotencyKey")[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Only_one_attempt_per_due_is_open_and_an_expired_one_is_replaced()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var first = await StartOnlineOkAsync(enrollment);

        var second = await StartOnlineAsync(enrollment, "2027-01", "second-key-1");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(second)).ShouldBe("OnlinePayment.CheckoutInProgress");

        using var later = AdvanceTo(Api.Clock.GetUtcNow().AddMinutes(31));
        var third = await StartOnlineAsync(enrollment, "2027-01", "third-key-1", later);
        third.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await OnlinePaymentAsync(IdOf(first), later)).GetProperty("status").GetString().ShouldBe("Expired");
    }

    [Fact]
    public async Task A_declined_payment_fails_and_the_due_can_be_paid_again()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);

        await CompleteSandboxAsync(TokenOf(started), succeed: false);
        var returned = await ReturnAsync(TokenOf(started));

        returned.Headers.Location!.OriginalString.ShouldBe("/odeme-sonucu?durum=Failed");
        var failed = await OnlinePaymentAsync(IdOf(started));
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("failureCode").GetString().ShouldBe("FakeDeclined");
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
        (await StartOnlineAsync(enrollment, "2027-01", "retry-key-1")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_provider_outage_while_opening_fails_the_attempt_without_charging()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        Fake.FailNextCalls(1);

        var response = await StartOnlineAsync(enrollment, "2027-01", "outage-key-1");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("OnlinePayment.ProviderUnavailable");
        var replay = await BodyOf(await StartOnlineAsync(enrollment, "2027-01", "outage-key-1"));
        replay.GetProperty("status").GetString().ShouldBe("Failed");
        (await StartOnlineAsync(enrollment, "2027-01", "outage-key-2")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Online_payments_cannot_be_cancelled_or_recorded_by_hand()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        await CompleteSandboxAsync(TokenOf(started));
        var paymentId = (await OnlinePaymentAsync(IdOf(started))).GetProperty("paymentId").GetGuid();

        var cancel = await CancelAsync(paymentId, "yanlış");
        cancel.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(cancel)).ShouldBe("Payment.OnlineNotCancellable");

        var manual = await RecordAsync(enrollment, "2027-01", "10.00", method: "Online");
        manual.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Online_payments_are_listed_by_due()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);

        var list = await BodyOf(await Admin.GetAsync(new Uri($"/api/v1/online-payments?enrollmentId={enrollment}&period=2027-01", UriKind.Relative), Ct));

        list.GetProperty("totalCount").GetInt32().ShouldBe(1);
        list.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(IdOf(started));
        list.GetProperty("items")[0].GetProperty("studentFirstName").GetString().ShouldBe("Ali");
        var none = await BodyOf(await Admin.GetAsync(new Uri("/api/v1/online-payments?status=Succeeded", UriKind.Relative), Ct));
        none.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }
}

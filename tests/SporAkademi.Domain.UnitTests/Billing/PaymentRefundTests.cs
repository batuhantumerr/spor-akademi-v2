using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Billing;

/// <summary>P3-13, DEC-417: the refund state machine returns an online payment's money at most once.</summary>
[Trait("Story", "US-23")]
[Trait("Roadmap", "P3-13")]
public sealed class PaymentRefundTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId Admin = EntityId.New<UserId>();
    private static readonly string Hash = new('b', 64);

    private static readonly PaidEnrollment Enrollment =
        new(EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(), new DateOnly(2026, 9, 1), null);

    private static (OnlinePayment Online, Payment Payment) Paid()
    {
        var online = OnlinePayment.Start(EntityId.New<SchoolId>(), Enrollment, January, Money.Try(1000m), "Fake", "anahtar-0001", Hash, Now).Value;
        online.OpenCheckout("tok", "/sandbox-odeme?token=tok", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        var payment = online.Succeed(new ProviderCharge("fp-1", Money.Try(1000m), Now, new DateOnly(2027, 1, 14))).Value!;
        return (online, payment);
    }

    private static PaymentRefund Requested(Payment payment, OnlinePayment online, string reason = "Kayıt erken bitti") =>
        PaymentRefund.Request(payment, online, reason, "iade-anahtari-1", Hash, Admin, Now).Value;

    [Fact]
    public void A_request_is_pending_for_the_full_amount_and_keeps_the_providers_charge()
    {
        var (online, payment) = Paid();

        var refund = Requested(payment, online, "  Kayıt erken bitti  ");

        refund.Status.ShouldBe(PaymentRefundStatus.Pending);
        refund.Amount.ShouldBe(payment.Amount);
        refund.ProviderPaymentId.ShouldBe("fp-1");
        refund.Provider.ShouldBe("Fake");
        refund.Reason.ShouldBe("Kayıt erken bitti");
        refund.PaymentId.ShouldBe(payment.Id);
        payment.Status.ShouldBe(PaymentStatus.Recorded);
    }

    [Fact]
    public void A_success_refunds_the_payment_once_and_raises_one_event()
    {
        var (online, payment) = Paid();
        var refund = Requested(payment, online);
        refund.RecordAttempt(Now);

        refund.Succeed(payment, "fr-1", Now).Value.ShouldBeTrue();
        refund.Succeed(payment, "fr-1", Now).Value.ShouldBeFalse();

        refund.Status.ShouldBe(PaymentRefundStatus.Succeeded);
        refund.ProviderRefundId.ShouldBe("fr-1");
        refund.Attempts.ShouldBe(1);
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        refund.DomainEvents.OfType<PaymentRefunded>().Count().ShouldBe(1);
        refund.Fail("late", Now).ShouldBeFalse();
        refund.Status.ShouldBe(PaymentRefundStatus.Succeeded);
    }

    [Fact]
    public void A_refusal_is_final_and_the_payment_still_counts()
    {
        var (online, payment) = Paid();
        var refund = Requested(payment, online);

        refund.Fail("FakeRefundDeclined", Now).ShouldBeTrue();

        refund.Status.ShouldBe(PaymentRefundStatus.Failed);
        refund.FailureCode.ShouldBe("FakeRefundDeclined");
        payment.Status.ShouldBe(PaymentStatus.Recorded);
        refund.Succeed(payment, "fr-1", Now).Error.ShouldBe(PaymentRefundErrors.NotPending);
        refund.Fail("again", Now).ShouldBeFalse();
    }

    [Fact]
    public void An_unknown_outcome_only_counts_the_attempt()
    {
        var (online, payment) = Paid();
        var refund = Requested(payment, online);

        refund.RecordAttempt(Now);
        refund.RecordAttempt(Now.AddMinutes(10));

        refund.Status.ShouldBe(PaymentRefundStatus.Pending);
        refund.Attempts.ShouldBe(2);
        refund.LastAttemptAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void A_refunded_payment_is_not_refunded_or_cancelled_again()
    {
        var (online, payment) = Paid();
        Requested(payment, online).Succeed(payment, "fr-1", Now).IsSuccess.ShouldBeTrue();

        PaymentRefund.Request(payment, online, "tekrar", "iade-anahtari-2", Hash, Admin, Now).Error.ShouldBe(PaymentRefundErrors.AlreadyRefunded);
        payment.Cancel("yanlış", Admin, Now).Error.ShouldBe(PaymentErrors.OnlineNotCancellable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_reason_is_required(string reason)
    {
        var (online, payment) = Paid();

        PaymentRefund.Request(payment, online, reason, "iade-anahtari-1", Hash, Admin, Now).Error.ShouldBe(PaymentRefundErrors.ReasonRequired);
    }

    [Fact]
    public void A_reason_has_a_maximum_length()
    {
        var (online, payment) = Paid();

        PaymentRefund.Request(payment, online, new string('x', PaymentRefund.ReasonMaxLength + 1), "iade-anahtari-1", Hash, Admin, Now)
            .Error.ShouldBe(CommonErrors.FieldTooLong);
    }

    [Fact]
    public void A_manual_payment_is_not_refunded()
    {
        var (online, _) = Paid();
        var cash = Payment.Record(online.TenantId, Enrollment, January, Money.Try(500m), PaymentMethod.Cash, new DateOnly(2027, 1, 10), feeDefined: true).Value;

        PaymentRefund.Request(cash, online, "iade", "iade-anahtari-1", Hash, Admin, Now).Error.ShouldBe(PaymentRefundErrors.NotOnline);
    }

    [Fact]
    public void A_payment_of_another_online_attempt_is_not_refundable()
    {
        var (online, _) = Paid();
        var (_, otherPayment) = Paid();

        PaymentRefund.Request(otherPayment, online, "iade", "iade-anahtari-1", Hash, Admin, Now).Error.ShouldBe(PaymentRefundErrors.NotRefundable);
    }

    [Fact]
    public void Only_a_request_in_review_is_settled_by_hand_with_a_note_and_its_author()
    {
        var (online, payment) = Paid();
        var refund = Requested(payment, online);
        refund.ResolveManually(payment, true, "panelde iade var", Admin, Now).Error.ShouldBe(PaymentRefundErrors.NotInReview);
        refund.MarkForReview().ShouldBeTrue();
        refund.MarkForReview().ShouldBeFalse();
        refund.ResolveManually(payment, true, "  ", Admin, Now).Error.ShouldBe(PaymentRefundErrors.ResolutionNoteRequired);

        refund.ResolveManually(payment, true, " panelde iade var ", Admin, Now).IsSuccess.ShouldBeTrue();

        refund.Status.ShouldBe(PaymentRefundStatus.Succeeded);
        refund.NeedsReview.ShouldBeFalse();
        refund.ResolvedBy.ShouldBe(Admin);
        refund.ResolutionNote.ShouldBe("panelde iade var");
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        refund.DomainEvents.OfType<PaymentRefunded>().Count().ShouldBe(1);
    }

    [Fact]
    public void A_request_in_review_settled_as_not_refunded_fails_and_keeps_the_payment()
    {
        var (online, payment) = Paid();
        var refund = Requested(payment, online);
        refund.MarkForReview();

        refund.ResolveManually(payment, false, "panelde iade yok", Admin, Now).IsSuccess.ShouldBeTrue();

        refund.Status.ShouldBe(PaymentRefundStatus.Failed);
        refund.FailureCode.ShouldBe(PaymentRefund.ManuallyFailedCode);
        payment.Status.ShouldBe(PaymentStatus.Recorded);
    }
}

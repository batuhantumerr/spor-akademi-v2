using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Billing;

/// <summary>P2-F1, DEC-342: the online payment state machine never credits a due twice.</summary>
[Trait("Story", "US-23")]
public sealed class OnlinePaymentTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly PaidEnrollment Enrollment =
        new(EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(), new DateOnly(2026, 9, 1), null);

    private static OnlinePayment Started(decimal amount = 1000m) =>
        OnlinePayment.Start(EntityId.New<SchoolId>(), Enrollment, January, Money.Try(amount), "Fake", "anahtar-0001", new string('a', 64), Now).Value;

    private static OnlinePayment Pending()
    {
        var payment = Started();
        payment.OpenCheckout("tok", "/sandbox-odeme?token=tok", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        return payment;
    }

    private static ProviderCharge Charge(string id = "pay-1", decimal amount = 1000m) => new(id, Money.Try(amount), Now.AddMinutes(5), new DateOnly(2027, 1, 14));

    [Fact]
    public void A_started_attempt_is_created_and_opens_once()
    {
        var payment = Started();
        payment.Status.ShouldBe(OnlinePaymentStatus.Created);

        payment.OpenCheckout("tok", "/x", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        payment.Status.ShouldBe(OnlinePaymentStatus.Pending);
        payment.OpenCheckout("tok-2", "/y", Now.AddMinutes(30)).Error.ShouldBe(OnlinePaymentErrors.NotOpen);
        payment.ProviderToken.ShouldBe("tok");
    }

    [Fact]
    public void Nothing_due_or_a_period_outside_the_enrollment_cannot_be_started()
    {
        OnlinePayment.Start(EntityId.New<SchoolId>(), Enrollment, January, Money.Zero, "Fake", "k-00000001", new string('a', 64), Now)
            .Error.ShouldBe(OnlinePaymentErrors.NothingDue);
        OnlinePayment.Start(EntityId.New<SchoolId>(), Enrollment, YearMonth.Create(2026, 8).Value, Money.Try(1m), "Fake", "k-00000001", new string('a', 64), Now)
            .Error.ShouldBe(PaymentErrors.EnrollmentNotActiveInPeriod);
    }

    [Fact]
    public void The_first_success_records_one_online_payment_and_repeats_record_nothing()
    {
        var payment = Pending();

        var first = payment.Succeed(Charge());
        var repeat = payment.Succeed(Charge());

        var recorded = first.Value.ShouldNotBeNull();
        recorded.Method.ShouldBe(PaymentMethod.Online);
        recorded.OnlinePaymentId.ShouldBe(payment.Id);
        recorded.Amount.ShouldBe(Money.Try(1000m));
        recorded.Period.ShouldBe(January);
        payment.PaymentId.ShouldBe(recorded.Id);
        payment.Status.ShouldBe(OnlinePaymentStatus.Succeeded);
        repeat.IsSuccess.ShouldBeTrue();
        repeat.Value.ShouldBeNull();
        payment.DomainEvents.OfType<OnlinePaymentSucceeded>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Another_charge_or_amount_than_the_attempt_is_rejected()
    {
        var payment = Pending();
        payment.Succeed(Charge(amount: 999m)).Error.ShouldBe(OnlinePaymentErrors.ProviderMismatch);
        payment.Status.ShouldBe(OnlinePaymentStatus.Pending);

        payment.Succeed(Charge()).IsSuccess.ShouldBeTrue();
        payment.Succeed(Charge("pay-2")).Error.ShouldBe(OnlinePaymentErrors.ProviderMismatch);
    }

    [Fact]
    public void A_failure_never_undoes_a_success_but_a_late_success_after_a_failure_is_credited()
    {
        var succeeded = Pending();
        succeeded.Succeed(Charge());
        succeeded.Fail("Declined", Now).ShouldBeFalse();
        succeeded.Status.ShouldBe(OnlinePaymentStatus.Succeeded);

        var failed = Pending();
        failed.Fail("Declined", Now).ShouldBeTrue();
        failed.Fail("Declined", Now).ShouldBeFalse();
        failed.Succeed(Charge()).Value.ShouldNotBeNull();
        failed.Status.ShouldBe(OnlinePaymentStatus.Succeeded);
        failed.FailureCode.ShouldBeNull();
    }

    [Fact]
    public void An_open_attempt_expires_with_its_checkout()
    {
        var created = Started();
        created.HasExpired(Now.AddMinutes(29), TimeSpan.FromMinutes(30)).ShouldBeFalse();
        created.HasExpired(Now.AddMinutes(30), TimeSpan.FromMinutes(30)).ShouldBeTrue();

        var pending = Pending();
        pending.HasExpired(Now.AddMinutes(31), TimeSpan.FromHours(5)).ShouldBeTrue();
        pending.Succeed(Charge());
        pending.HasExpired(Now.AddDays(1), TimeSpan.FromMinutes(30)).ShouldBeFalse();
    }

    [Fact]
    public void An_online_payment_cannot_be_cancelled_or_recorded_by_hand()
    {
        var payment = Pending();
        var recorded = payment.Succeed(Charge()).Value!;

        recorded.Cancel("yanlış", EntityId.New<UserId>(), Now).Error.ShouldBe(PaymentErrors.OnlineNotCancellable);
        Payment.Record(EntityId.New<SchoolId>(), Enrollment, January, Money.Try(1m), PaymentMethod.Online, new DateOnly(2027, 1, 10), feeDefined: true)
            .IsFailure.ShouldBeTrue();
    }
}

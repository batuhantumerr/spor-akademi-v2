using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Billing;

[Trait("Story", "US-23")]
public sealed class PaymentTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    internal static Payment NewPayment(decimal amount = 1000m) =>
        Payment.Record(
            EntityId.New<SchoolId>(),
            new PaidEnrollment(EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(), new DateOnly(2026, 9, 1), null),
            January,
            Money.Try(amount),
            PaymentMethod.Cash,
            new DateOnly(2027, 1, 10),
            feeDefined: true).Value;

    [Fact]
    public void Recorded_payment_keeps_its_data()
    {
        var payment = NewPayment();

        payment.Status.ShouldBe(PaymentStatus.Recorded);
        payment.Amount.ShouldBe(Money.Try(1000m));
        payment.Period.ShouldBe(January);
        payment.Id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Amount_must_be_greater_than_zero()
    {
        var enrollment = new PaidEnrollment(EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(), new DateOnly(2026, 9, 1), null);

        Payment.Record(EntityId.New<SchoolId>(), enrollment, January, Money.Zero, PaymentMethod.Cash, new DateOnly(2027, 1, 10), true)
            .Error.ShouldBe(PaymentErrors.InvalidAmount);
    }

    [Theory]
    [InlineData("2027-01-31", null, true)]
    [InlineData("2027-02-01", null, false)]
    [InlineData("2026-09-01", "2027-01-01", true)]
    [InlineData("2026-09-01", "2026-12-31", false)]
    public void The_enrollment_must_be_active_at_least_one_day_of_the_period(string start, string? end, bool allowed)
    {
        var enrollment = new PaidEnrollment(
            EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(),
            DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            end is null ? null : DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture));

        var result = Payment.Record(EntityId.New<SchoolId>(), enrollment, January, Money.Try(10m), PaymentMethod.BankTransfer, new DateOnly(2027, 1, 10), true);

        if (allowed)
        {
            result.IsSuccess.ShouldBeTrue();
        }
        else
        {
            result.Error.ShouldBe(PaymentErrors.EnrollmentNotActiveInPeriod);
        }
    }

    [Fact]
    public void A_fee_must_be_defined_for_the_period()
    {
        var enrollment = new PaidEnrollment(EntityId.New<EnrollmentId>(), EntityId.New<StudentId>(), EntityId.New<GroupId>(), new DateOnly(2026, 9, 1), null);

        Payment.Record(EntityId.New<SchoolId>(), enrollment, January, Money.Try(10m), PaymentMethod.Cash, new DateOnly(2027, 1, 10), feeDefined: false)
            .Error.ShouldBe(PaymentErrors.FeeNotDefined);
    }

    [Fact]
    public void Cancelling_needs_a_reason_and_records_who_and_when_once()
    {
        var payment = NewPayment();
        var admin = EntityId.New<UserId>();

        payment.Cancel("  ", admin, Now).Error.ShouldBe(PaymentErrors.CancelReasonRequired);
        payment.Cancel("  Yanlış ay girildi ", admin, Now).IsSuccess.ShouldBeTrue();
        payment.Cancel("again", admin, Now).Error.ShouldBe(PaymentErrors.AlreadyCancelled);

        payment.Status.ShouldBe(PaymentStatus.Cancelled);
        payment.CancelReason.ShouldBe("Yanlış ay girildi");
        payment.CancelledBy.ShouldBe(admin);
        payment.CancelledAt.ShouldBe(Now);
        payment.Amount.ShouldBe(Money.Try(1000m));
    }

    [Fact]
    public void Cancel_reason_is_bounded()
    {
        NewPayment().Cancel(new string('x', Payment.CancelReasonMaxLength + 1), EntityId.New<UserId>(), Now)
            .Error.ShouldBe(CommonErrors.FieldTooLong);
    }
}

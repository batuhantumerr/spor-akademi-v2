using NSubstitute;
using SporAkademi.Application.Billing;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Billing;

/// <summary>DEC-373: one service answers "what does this enrollment owe", for a whole window or for one due.</summary>
[Trait("Story", "P2-F8")]
public sealed class DueChargesTests
{
    private static readonly YearMonth January = YearMonth.Create(2027, 1).Value;
    private static readonly GroupId U10 = EntityId.New<GroupId>();
    private static readonly GroupId U12 = EntityId.New<GroupId>();
    private static readonly StudentId Ali = EntityId.New<StudentId>();
    private static readonly StudentId Veli = EntityId.New<StudentId>();
    private static readonly EnrollmentInfo AliInU10 = new(EntityId.New<EnrollmentId>(), Ali, U10, new DateOnly(2026, 9, 1), null);
    private static readonly EnrollmentInfo VeliInU12 = new(EntityId.New<EnrollmentId>(), Veli, U12, new DateOnly(2026, 9, 1), null);

    private readonly IEnrollmentDirectory _enrollments = Substitute.For<IEnrollmentDirectory>();
    private readonly IGroupFeeSchedule _fees = Substitute.For<IGroupFeeSchedule>();
    private readonly IBillingRulesRepository _rules = Substitute.For<IBillingRulesRepository>();
    private readonly IStudentSiblings _siblings = Substitute.For<IStudentSiblings>();

    public DueChargesTests()
    {
        _enrollments.ListActiveBetweenAsync(null, Arg.Any<DateOnly?>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([AliInU10, VeliInU12]);
        _enrollments.ListOfStudentsActiveBetweenAsync(Arg.Any<IReadOnlyCollection<StudentId>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([AliInU10, VeliInU12]);
        _fees.GetFeesAsync(Arg.Any<IReadOnlyCollection<GroupId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<GroupId, IReadOnlyList<GroupFeeInfo>>
            {
                [U10] = [new GroupFeeInfo(January, Money.Try(800m))],
                [U12] = [new GroupFeeInfo(January, Money.Try(1000m))],
            });
        IReadOnlyList<StudentId> family = [.. new[] { Ali, Veli }.OrderBy(s => s.Value)];
        _siblings.GetSiblingSetsAsync(Arg.Any<IReadOnlyCollection<StudentId>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<StudentId, IReadOnlyList<StudentId>> { [Ali] = family, [Veli] = family });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DueCharges Service() => new(_enrollments, _fees, _rules, _siblings);

    private void WithRules(params BillingRuleTerms[] versions) =>
        _rules.ListAsync(Arg.Any<CancellationToken>())
            .Returns([.. versions.Select(v => BillingRules.Define(EntityId.New<SchoolId>(), v, v.EffectiveFrom).Value)]);

    [Fact]
    public async Task Without_rules_the_sibling_sets_are_not_read_and_fees_are_unchanged()
    {
        WithRules();

        var book = await Service().ForSchoolAsync(January, January, Ct);
        var single = await Service().ForEnrollmentAsync(AliInU10, January, Ct);

        book.Charges.Select(c => c.Fee.Amount).ShouldBe([800m, 1000m]);
        single!.Fee.Amount.ShouldBe(800m);
        await _siblings.DidNotReceiveWithAnyArgs().GetSiblingSetsAsync(default!, Ct);
        await _enrollments.DidNotReceiveWithAnyArgs().ListOfStudentsActiveBetweenAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task One_due_is_ranked_among_its_siblings_exactly_like_the_school_window()
    {
        WithRules(new BillingRuleTerms(January, 10, 15, true));

        var book = await Service().ForSchoolAsync(January, January, Ct);
        var single = await Service().ForEnrollmentAsync(AliInU10, January, Ct);

        book.Charges.Single(c => c.Enrollment.Id == AliInU10.Id).ShouldBe(single);
        (single!.ListFee.Amount, single.Fee.Amount, single.Adjustment).ShouldBe((800m, 720m, ChargeAdjustment.SiblingDiscount));
        // The siblings' enrollments of the period come in one query.
        await _enrollments.Received(1).ListOfStudentsActiveBetweenAsync(
            Arg.Is<IReadOnlyCollection<StudentId>>(s => s.Count == 2 && s.Contains(Ali) && s.Contains(Veli)),
            January.FirstDay,
            January.LastDay,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_version_that_starts_after_the_window_does_not_read_the_sibling_sets()
    {
        WithRules(new BillingRuleTerms(YearMonth.Create(2027, 3).Value, 10, 15, true));

        var book = await Service().ForSchoolAsync(January, January, Ct);

        book.Charges.ShouldAllBe(c => c.Adjustment == ChargeAdjustment.None);
        await _siblings.DidNotReceiveWithAnyArgs().GetSiblingSetsAsync(default!, Ct);
    }

    [Fact]
    public async Task An_enrollment_not_active_in_the_period_owes_nothing()
    {
        WithRules();
        var later = AliInU10 with { StartDate = new DateOnly(2027, 2, 1) };

        (await Service().ForEnrollmentAsync(later, January, Ct)).ShouldBeNull();
    }
}

using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>One version of the school's fee rules (P2-F8, DEC-370): from <see cref="EffectiveFrom"/> until the next version.</summary>
public sealed record BillingRulesDto(
    Guid Id,
    string EffectiveFrom,
    int SecondChildPercent,
    int ThirdAndLaterChildPercent,
    bool SingleChargeOnGroupChange);

/// <summary>Defines a fee rules version from <see cref="EffectiveFrom"/> (<c>YYYY-MM</c>) on; the current school only.</summary>
public sealed record DefineBillingRulesCommand(
    string? EffectiveFrom,
    int? SecondChildPercent,
    int? ThirdAndLaterChildPercent,
    bool? SingleChargeOnGroupChange) : ICommand<Unit>;

/// <summary>The current school's fee rules versions, newest first.</summary>
public sealed record ListBillingRulesQuery : IQuery<IReadOnlyList<BillingRulesDto>>;

/// <summary>Fee rules versions of the current school (tenant filter applies). Never updated or deleted.</summary>
public interface IBillingRulesRepository
{
    /// <summary>Every version, newest <see cref="BillingRules.EffectiveFrom"/> first. A school has a handful.</summary>
    Task<IReadOnlyList<BillingRules>> ListAsync(CancellationToken cancellationToken);

    Task<bool> ExistsAsync(YearMonth effectiveFrom, CancellationToken cancellationToken);

    void Add(BillingRules rules);
}

internal sealed class DefineBillingRulesValidator : AbstractValidator<DefineBillingRulesCommand>
{
    public DefineBillingRulesValidator()
    {
        RuleFor(c => c.EffectiveFrom).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code);
        RuleFor(c => c.SecondChildPercent).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(p => BillingRules.IsValidPercent(p!.Value)).WithErrorCode(BillingRulesErrors.InvalidPercent.Code);
        RuleFor(c => c.ThirdAndLaterChildPercent).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(p => BillingRules.IsValidPercent(p!.Value)).WithErrorCode(BillingRulesErrors.InvalidPercent.Code);
        RuleFor(c => c.SingleChargeOnGroupChange).NotNull().WithErrorCode(CommonErrors.FieldRequired.Code);
    }
}

/// <summary>
/// DEC-370: like a group fee, a version starts in the school's current period or later and each period has at most one
/// version (checked here and by the unique index, so a concurrent second definition gets the same conflict).
/// </summary>
internal sealed class DefineBillingRulesHandler(IBillingRulesRepository rules, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<DefineBillingRulesCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DefineBillingRulesCommand command, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var terms = new BillingRuleTerms(
            YearMonth.Parse(command.EffectiveFrom!).Value,
            command.SecondChildPercent!.Value,
            command.ThirdAndLaterChildPercent!.Value,
            command.SingleChargeOnGroupChange!.Value);
        var currentPeriod = YearMonth.From(await tenancy.TodayAsync(tenant, cancellationToken));

        var defined = BillingRules.Define(tenant, terms, currentPeriod);
        if (defined.IsFailure)
        {
            return Result.Failure<Unit>(defined.Error);
        }

        if (await rules.ExistsAsync(terms.EffectiveFrom, cancellationToken))
        {
            return Result.Failure<Unit>(BillingRulesErrors.AlreadyDefined);
        }

        rules.Add(defined.Value);
        return Result.Success(Unit.Value);
    }
}

internal sealed class ListBillingRulesHandler(IBillingRulesRepository rules) : IQueryHandler<ListBillingRulesQuery, IReadOnlyList<BillingRulesDto>>
{
    public async Task<Result<IReadOnlyList<BillingRulesDto>>> Handle(ListBillingRulesQuery query, CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<BillingRulesDto>>(
        [
            .. (await rules.ListAsync(cancellationToken)).Select(r => new BillingRulesDto(
                r.Id.Value,
                r.EffectiveFrom.ToString(),
                r.SecondChildPercent,
                r.ThirdAndLaterChildPercent,
                r.SingleChargeOnGroupChange)),
        ]);
}

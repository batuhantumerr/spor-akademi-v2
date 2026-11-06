using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Behaviors;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Groups;

/// <summary>US-22: a monthly fee from <see cref="EffectiveFrom"/> (<c>YYYY-MM</c>) onward.</summary>
public sealed record DefineGroupFeeCommand(GroupId Id, string EffectiveFrom, MoneyDto Amount) : ICommand<Unit>;

public sealed record ListGroupFeesQuery(GroupId Id) : IQuery<IReadOnlyList<GroupFeeDto>>;

internal sealed class DefineGroupFeeValidator : AbstractValidator<DefineGroupFeeCommand>
{
    public DefineGroupFeeValidator()
    {
        RuleFor(c => c.EffectiveFrom).Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(p => YearMonth.Parse(p).IsSuccess).WithErrorCode(YearMonth.Invalid.Code);
        RuleFor(c => c.Amount).Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode(CommonErrors.FieldRequired.Code)
            .Must(a => a.ToMoney().IsSuccess).WithErrorCode(Money.Invalid.Code);
    }
}

internal sealed class DefineGroupFeeHandler(IGroupRepository groups, ITenancyModule tenancy, ICurrentUser currentUser)
    : ICommandHandler<DefineGroupFeeCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DefineGroupFeeCommand command, CancellationToken cancellationToken)
    {
        var group = await groups.GetWithFeesAsync(command.Id, cancellationToken);
        if (group is null)
        {
            return Result.Failure<Unit>(GroupErrors.NotFound);
        }

        var period = YearMonth.Parse(command.EffectiveFrom);
        var amount = command.Amount.ToMoney();
        if (period.IsFailure || amount.IsFailure)
        {
            return Result.Failure<Unit>(period.IsFailure
                ? ValidationErrors.ForField("effectiveFrom", period.Error)
                : ValidationErrors.ForField("amount", amount.Error));
        }

        var today = await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken);
        // DEC-184: the fee row alone would not touch the group row, so a concurrent archive could slip in after the
        // check below; the version-checked group update turns that race into Concurrency.Conflict.
        groups.ExpectUnchangedSinceLoad(group);
        var defined = group.DefineFee(period.Value, amount.Value, YearMonth.From(today));
        return defined.IsSuccess ? Result.Success(Unit.Value) : Result.Failure<Unit>(defined.Error);
    }
}

internal sealed class ListGroupFeesHandler(IGroupRepository groups) : IQueryHandler<ListGroupFeesQuery, IReadOnlyList<GroupFeeDto>>
{
    public async Task<Result<IReadOnlyList<GroupFeeDto>>> Handle(ListGroupFeesQuery query, CancellationToken cancellationToken) =>
        await groups.FindFeesAsync(query.Id, cancellationToken) is { } fees
            ? Result.Success<IReadOnlyList<GroupFeeDto>>([.. fees.Select(f => new GroupFeeDto(f.EffectiveFrom.ToString(), MoneyDto.From(f.Amount)))])
            : Result.Failure<IReadOnlyList<GroupFeeDto>>(GroupErrors.NotFound);
}

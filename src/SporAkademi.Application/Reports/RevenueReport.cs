using System.Globalization;
using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Reports;

/// <summary>
/// A fee period of the revenue report (DEC-408), or with a null <see cref="Period"/> the range total. See
/// <see cref="PeriodRevenue"/> for the amounts. <see cref="CollectionRate"/> is the settled share of the accrual,
/// (accrued − outstanding) / accrued with 4 decimals, null when nothing accrued.
/// </summary>
public sealed record RevenuePeriodDto(
    string? Period,
    MoneyDto ListFees,
    MoneyDto SiblingDiscount,
    MoneyDto GroupChangeWaived,
    MoneyDto Accrued,
    MoneyDto CollectedForPeriod,
    MoneyDto Outstanding,
    MoneyDto CollectedInMonth,
    decimal? CollectionRate,
    int Charges,
    int SiblingDiscountedCharges);

/// <summary>Unpaid dues <see cref="FromDays"/> to <see cref="ToDays"/> days (no upper bound when null) past their due date.</summary>
public sealed record OverdueBucketDto(int FromDays, int? ToDays, int Dues, MoneyDto Amount);

/// <summary>
/// The revenue report (P3-09, DEC-408): accrual and collection per fee period with the fee rules' effect, and today's
/// unpaid dues of the dues window aged from their due date (day <see cref="DueDay"/> of the period's month).
/// </summary>
public sealed record RevenueReportDto(
    string From,
    string To,
    DateOnly Today,
    int DueDay,
    IReadOnlyList<RevenuePeriodDto> Periods,
    RevenuePeriodDto Total,
    IReadOnlyList<OverdueBucketDto> Overdue,
    int OverdueDues,
    MoneyDto OverdueAmount,
    int NotYetDueDues,
    MoneyDto NotYetDueAmount);

public sealed record GetRevenueReportQuery(string? From, string? To) : IQuery<RevenueReportDto>;

internal sealed class GetRevenueReportValidator : AbstractValidator<GetRevenueReportQuery>
{
    public GetRevenueReportValidator() => ReportInput.AddRangeRules(this, q => q.From, q => q.To);
}

internal sealed class GetRevenueReportHandler(
    IBillingReport billing,
    ITenancyModule tenancy,
    ITenantCache cache,
    ICurrentUser currentUser) : IQueryHandler<GetRevenueReportQuery, RevenueReportDto>
{
    public async Task<Result<RevenueReportDto>> Handle(GetRevenueReportQuery query, CancellationToken cancellationToken)
    {
        var tenant = currentUser.RequireTenant();
        var today = await tenancy.TodayAsync(tenant, cancellationToken);
        var range = ReportRange.Resolve(query.From, query.To, YearMonth.From(today));
        if (range.IsFailure)
        {
            return Result.Failure<RevenueReportDto>(range.Error);
        }

        var (from, to) = (range.Value.From, range.Value.To);
        return Result.Success(await cache.GetOrCreateAsync(
            TenantCacheKey.For(tenant, CacheArea.Reports, "revenue", from.FirstDay, to.LastDay, today),
            async ct => Build(await billing.RevenueAsync(from, to, today, ct), range.Value, today),
            cancellationToken));
    }

    private static RevenueReportDto Build(RevenueFigures figures, ReportRange range, DateOnly today)
    {
        var periods = figures.Periods.Select(p => Period(p.Period.ToString(), [p])).ToList();
        return new RevenueReportDto(
            range.From.ToString(),
            range.To.ToString(),
            today,
            figures.DueDay,
            periods,
            Period(null, figures.Periods),
            [.. figures.Overdue.Select(b => new OverdueBucketDto(b.FromDays, b.ToDays, b.Dues, Money(b.Amount)))],
            figures.Overdue.Sum(b => b.Dues),
            Money(figures.Overdue.Sum(b => b.Amount)),
            figures.NotYetDueDues,
            Money(figures.NotYetDueAmount));
    }

    private static RevenuePeriodDto Period(string? period, IReadOnlyCollection<PeriodRevenue> months)
    {
        var accrued = months.Sum(m => m.Accrued);
        var outstanding = months.Sum(m => m.Outstanding);
        return new RevenuePeriodDto(
            period,
            Money(months.Sum(m => m.ListFees)),
            Money(months.Sum(m => m.SiblingDiscount)),
            Money(months.Sum(m => m.GroupChangeWaived)),
            Money(accrued),
            Money(months.Sum(m => m.CollectedForPeriod)),
            Money(outstanding),
            Money(months.Sum(m => m.CollectedInMonth)),
            ReportRows.Ratio(accrued - outstanding, accrued),
            months.Sum(m => m.Charges),
            months.Sum(m => m.SiblingDiscountedCharges));
    }

    /// <summary>Report totals are exact decimals in the API's money shape; they are never limited to one payment's maximum.</summary>
    private static MoneyDto Money(decimal amount) =>
        new(decimal.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), Domain.SharedKernel.Money.TryCurrency);
}

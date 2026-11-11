using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Billing;

/// <summary>
/// US-24: unpaid dues of the last 24 months up to the school's current month, or of one period (<c>YYYY-MM</c>),
/// optionally of one group. Oldest period first, then group and student name.
/// </summary>
public sealed record ListOutstandingDuesQuery(Guid? GroupId, string? Period, PageRequest Page) : IQuery<PagedResult<OutstandingDueDto>>;

internal sealed class ListOutstandingDuesValidator : AbstractValidator<ListOutstandingDuesQuery>
{
    public ListOutstandingDuesValidator()
    {
        RuleFor(q => q.Page.Page).InclusiveBetween(1, PageRequest.MaxPage).WithErrorCode("Field.Invalid").OverridePropertyName("page");
        RuleFor(q => q.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid").OverridePropertyName("pageSize");
        RuleFor(q => q.Period).Must(BillingInput.IsPeriod).WithErrorCode(YearMonth.Invalid.Code).When(q => q.Period is not null);
    }
}

internal sealed class ListOutstandingDuesHandler(
    OutstandingDues outstanding,
    ITenancyModule tenancy,
    ICurrentUser currentUser) : IQueryHandler<ListOutstandingDuesQuery, PagedResult<OutstandingDueDto>>
{
    public async Task<Result<PagedResult<OutstandingDueDto>>> Handle(ListOutstandingDuesQuery query, CancellationToken cancellationToken)
    {
        var currentPeriod = YearMonth.From(await tenancy.TodayAsync(currentUser.RequireTenant(), cancellationToken));
        var period = query.Period is { } text ? YearMonth.Parse(text).Value : null;
        if (period is not null && period > currentPeriod)
        {
            return Result.Success(new PagedResult<OutstandingDueDto>([], query.Page.Page, query.Page.PageSize, 0));
        }

        // DEC-183: without a period the list covers the last DuesHorizonMonths months, so the work is bounded by the
        // window, not by the school's whole history; every query below is limited to it.
        var from = period ?? OutstandingDues.HorizonStart(currentPeriod);
        var to = period ?? currentPeriod;
        var groupId = query.GroupId is { } id ? GroupId.From(id) : (GroupId?)null;
        var rows = await outstanding.ListAsync(from, to, groupId, c => groupId is null || c.Enrollment.GroupId == groupId, cancellationToken);

        var page = rows.Skip(query.Page.Skip).Take(query.Page.PageSize).ToList();
        return Result.Success(new PagedResult<OutstandingDueDto>(page, query.Page.Page, query.Page.PageSize, rows.Count));
    }

    /// <summary>How far back the unfiltered list looks (DEC-183): two years, like the default personal data retention.</summary>
    public const int DuesHorizonMonths = OutstandingDues.HorizonMonths;
}

/// <summary>
/// The outstanding dues rows of a window (US-24), shared by the School Admin's list and the parent's (P3-03, DEC-385),
/// so both show the same amounts: the charges come from <see cref="DueCharges"/> for the whole school (siblings rank
/// across groups, DEC-373) and only then is the result filtered.
/// </summary>
internal sealed class OutstandingDues(DueCharges charges, IPaymentRepository payments, IStudentDirectory students, IGroupDirectory groups)
{
    /// <summary>How far back an unfiltered list looks (DEC-183): two years, like the default personal data retention.</summary>
    public const int HorizonMonths = 24;

    public static YearMonth HorizonStart(YearMonth currentPeriod)
    {
        ArgumentNullException.ThrowIfNull(currentPeriod);
        return YearMonth.From(currentPeriod.FirstDay.AddMonths(1 - HorizonMonths));
    }

    /// <param name="paymentsOfGroup">Narrows the payment totals read (an optimisation only; <paramref name="include"/> decides the rows).</param>
    /// <param name="include">Which charges become rows.</param>
    public async Task<List<OutstandingDueDto>> ListAsync(
        YearMonth from,
        YearMonth to,
        GroupId? paymentsOfGroup,
        Func<EffectiveCharge, bool> include,
        CancellationToken cancellationToken,
        StudentId? familyOf = null)
    {
        // A single family's rows need only its own charges: the same amounts, without reading the whole school (DEC-388).
        var book = familyOf is { } student
            ? await charges.ForFamilyAsync(student, from, to, cancellationToken)
            : await charges.ForSchoolAsync(from, to, cancellationToken);
        var paid = familyOf is null
            ? await payments.RecordedTotalsAsync(paymentsOfGroup, from, to, cancellationToken)
            : await payments.RecordedTotalsOfEnrollmentsAsync([.. book.Enrollments.Keys], from, to, cancellationToken);
        var dues = FeeRules.Outstanding(book.Charges.Where(include), paid)
            .Where(d => d.Charge.Period >= from && d.Charge.Period <= to)
            .ToList();

        var studentNames = await students.GetManyAsync([.. dues.Select(d => d.Charge.StudentId.Value).Distinct()], cancellationToken);
        var groupNames = await groups.GetManyAsync([.. dues.Select(d => d.Charge.Enrollment.GroupId.Value).Distinct()], cancellationToken);

        return dues
            .Select(d =>
            {
                var charge = d.Charge;
                var student = studentNames.GetValueOrDefault(charge.StudentId.Value);
                return new OutstandingDueDto(
                    charge.Enrollment.Id.Value,
                    charge.StudentId.Value,
                    student?.FirstName ?? string.Empty,
                    student?.LastName ?? string.Empty,
                    charge.Enrollment.GroupId.Value,
                    groupNames.GetValueOrDefault(charge.Enrollment.GroupId.Value)?.Name ?? string.Empty,
                    charge.Period.ToString(),
                    MoneyDto.From(charge.Fee),
                    MoneyDto.From(d.Paid),
                    MoneyDto.From(d.Remaining),
                    MoneyDto.From(charge.ListFee),
                    MoneyDto.From(charge.Discount),
                    charge.Adjustment,
                    charge.DiscountPercent);
            })
            .OrderBy(r => r.Period, StringComparer.Ordinal)
            .ThenBy(r => r.GroupName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.StudentLastName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.StudentFirstName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.EnrollmentId)
            .ToList();
    }

    private static readonly System.Globalization.CultureInfo TurkishCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
}

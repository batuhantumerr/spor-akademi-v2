using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Groups.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;

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
    IEnrollmentDirectory enrollments,
    IGroupFeeSchedule fees,
    IPaymentRepository payments,
    IStudentDirectory students,
    IGroupDirectory groups,
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
        var from = period ?? YearMonth.From(currentPeriod.FirstDay.AddMonths(1 - DuesHorizonMonths));
        var to = period ?? currentPeriod;
        var groupId = query.GroupId is { } id ? GroupId.From(id) : (GroupId?)null;
        var active = await enrollments.ListActiveBetweenAsync(groupId, from.FirstDay, to.LastDay, cancellationToken);

        var groupFees = await fees.GetFeesAsync([.. active.Select(e => e.GroupId).Distinct()], cancellationToken);
        var rates = groupFees.ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<FeeRate>)[.. g.Value.Select(f => new FeeRate(f.EffectiveFrom, f.Amount))]);
        var paid = await payments.RecordedTotalsAsync(groupId, from, to, cancellationToken);

        var dues = DueCalculator.Outstanding(
                active.Select(e => new DueEnrollment(e.Id, e.GroupId, e.StartDate, e.EndDate)),
                rates,
                paid,
                to,
                from)
            .Where(d => period is null || d.Period == period)
            .ToList();

        var studentOf = active.ToDictionary(e => e.Id, e => e.StudentId.Value);
        var studentNames = await students.GetManyAsync([.. dues.Select(d => studentOf[d.EnrollmentId]).Distinct()], cancellationToken);
        var groupNames = await groups.GetManyAsync([.. dues.Select(d => d.GroupId.Value).Distinct()], cancellationToken);

        var rows = dues
            .Select(d =>
            {
                var studentId = studentOf[d.EnrollmentId];
                var student = studentNames.GetValueOrDefault(studentId);
                return new OutstandingDueDto(
                    d.EnrollmentId.Value,
                    studentId,
                    student?.FirstName ?? string.Empty,
                    student?.LastName ?? string.Empty,
                    d.GroupId.Value,
                    groupNames.GetValueOrDefault(d.GroupId.Value)?.Name ?? string.Empty,
                    d.Period.ToString(),
                    MoneyDto.From(d.Fee),
                    MoneyDto.From(d.Paid),
                    MoneyDto.From(d.Remaining));
            })
            .OrderBy(r => r.Period, StringComparer.Ordinal)
            .ThenBy(r => r.GroupName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.StudentLastName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.StudentFirstName, StringComparer.Create(TurkishCulture, ignoreCase: true))
            .ThenBy(r => r.EnrollmentId)
            .ToList();

        var page = rows.Skip(query.Page.Skip).Take(query.Page.PageSize).ToList();
        return Result.Success(new PagedResult<OutstandingDueDto>(page, query.Page.Page, query.Page.PageSize, rows.Count));
    }

    /// <summary>How far back the unfiltered list looks (DEC-183): two years, like the default personal data retention.</summary>
    public const int DuesHorizonMonths = 24;

    private static readonly System.Globalization.CultureInfo TurkishCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
}

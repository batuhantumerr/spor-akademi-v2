using FluentValidation;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Abstractions;

/// <summary>Offset paging (appendix D §1.6): default 20, at most 100 per page.</summary>
public sealed record PageRequest(int Page = 1, int PageSize = PageRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int Skip => (Page - 1) * PageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public sealed class PageRequestValidator : AbstractValidator<PageRequest>
{
    public PageRequestValidator()
    {
        RuleFor(p => p.Page).GreaterThanOrEqualTo(1).WithErrorCode("Field.Invalid");
        RuleFor(p => p.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize).WithErrorCode("Field.Invalid");
    }
}

public static class CurrentUserExtensions
{
    /// <summary>The caller's school. School-scoped endpoints require a school role, so a missing tenant is a programmer error.</summary>
    public static SchoolId RequireTenant(this ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        return currentUser.TenantId is { } tenant
            ? SchoolId.From(tenant)
            : throw new InvalidOperationException("This operation needs a school-bound user.");
    }
}

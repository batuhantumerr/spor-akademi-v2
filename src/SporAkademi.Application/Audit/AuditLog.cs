using FluentValidation;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Auditing;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Audit;

/// <summary>An audit entry as the School Admin sees it (P4-05): ids, names of fields, no values.</summary>
/// <param name="UserName">The actor's name when the actor is a user of the school; <c>null</c> for system work.</param>
public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid? UserId,
    string? UserName,
    string? UserRole,
    AuditAction Action,
    AuditEntityType EntityType,
    Guid? EntityId,
    IReadOnlyList<string> Fields,
    string? Reason,
    string? CorrelationId);

/// <summary>Someone who appears in the school's audit log, for the user filter.</summary>
public sealed record AuditActorDto(Guid UserId, string? UserName, string? UserRole);

/// <summary>A half-open interval of instants: <c>From &lt;= occurred_at &lt; Until</c>.</summary>
public sealed record AuditLogFilter(AuditEntityType? EntityType, Guid? UserId, DateTimeOffset? From, DateTimeOffset? Until);

/// <param name="EntityType">An <see cref="AuditEntityType"/> name.</param>
/// <param name="From">First day, in the school's time zone.</param>
/// <param name="To">Last day (inclusive), in the school's time zone.</param>
public sealed record ListAuditLogQuery(string? EntityType, Guid? UserId, DateOnly? From, DateOnly? To, PageRequest Page)
    : IQuery<PagedResult<AuditEntryDto>>;

public sealed record ListAuditActorsQuery : IQuery<IReadOnlyList<AuditActorDto>>;

/// <summary>The audit log of the current school (tenant filter and row level security apply), newest first.</summary>
public interface IAuditLogReadModel
{
    Task<PagedResult<AuditEntryDto>> ListAsync(AuditLogFilter filter, PageRequest page, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuditActorDto>> ListActorsAsync(CancellationToken cancellationToken);
}

internal sealed class ListAuditLogValidator : AbstractValidator<ListAuditLogQuery>
{
    public static readonly DateOnly EarliestDay = new(2000, 1, 1);
    public static readonly DateOnly LatestDay = new(2100, 12, 31);

    public ListAuditLogValidator()
    {
        RuleFor(q => q.EntityType)
            .Must(EnumNames.IsExact<AuditEntityType>)
            .When(q => !string.IsNullOrEmpty(q.EntityType))
            .WithErrorCode(CommonErrors.FieldInvalid.Code);
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From)
            .When(q => q.From is not null && q.To is not null)
            .WithErrorCode(CommonErrors.FieldInvalid.Code);
        // Days far outside the product's life would overflow the day arithmetic (0001-01-01, 9999-12-31).
        RuleFor(q => q.From).InclusiveBetween(EarliestDay, LatestDay).When(q => q.From is not null).WithErrorCode(CommonErrors.FieldInvalid.Code);
        RuleFor(q => q.To).InclusiveBetween(EarliestDay, LatestDay).When(q => q.To is not null).WithErrorCode(CommonErrors.FieldInvalid.Code);
        RuleFor(q => q.Page).SetValidator(new PageRequestValidator());
    }
}

internal sealed class ListAuditLogHandler(IAuditLogReadModel auditLog, ITenancyModule tenancy, ICurrentUser currentUser)
    : IQueryHandler<ListAuditLogQuery, PagedResult<AuditEntryDto>>
{
    public async Task<Result<PagedResult<AuditEntryDto>>> Handle(ListAuditLogQuery query, CancellationToken cancellationToken)
    {
        // Days are the school's days: 14 January means 00:00 to 24:00 in Istanbul, not in UTC.
        var zone = await tenancy.TimeZoneAsync(currentUser.RequireTenant(), cancellationToken);
        if (zone is null)
        {
            return Result.Failure<PagedResult<AuditEntryDto>>(SchoolErrors.NotFound);
        }

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var filter = new AuditLogFilter(
            string.IsNullOrEmpty(query.EntityType) ? null : Enum.Parse<AuditEntityType>(query.EntityType),
            query.UserId,
            query.From is { } from ? StartOf(from, timeZone) : null,
            query.To is { } to ? StartOf(to.AddDays(1), timeZone) : null);
        return Result.Success(await auditLog.ListAsync(filter, query.Page, cancellationToken));
    }

    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo timeZone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}

internal sealed class ListAuditActorsHandler(IAuditLogReadModel auditLog) : IQueryHandler<ListAuditActorsQuery, IReadOnlyList<AuditActorDto>>
{
    public async Task<Result<IReadOnlyList<AuditActorDto>>> Handle(ListAuditActorsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await auditLog.ListActorsAsync(cancellationToken));
}

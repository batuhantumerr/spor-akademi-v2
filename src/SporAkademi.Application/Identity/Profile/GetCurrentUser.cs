using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Identity.Profile;

public sealed record GetCurrentUserQuery(Guid UserId) : IQuery<UserSummary>;

/// <summary>The caller's profile, with the school's time zone read through the Tenancy contract (ADR-0009).</summary>
internal sealed class GetCurrentUserHandler(IUserRepository users, ITenancyModule tenancy) : IQueryHandler<GetCurrentUserQuery, UserSummary>
{
    public async Task<Result<UserSummary>> Handle(GetCurrentUserQuery query, CancellationToken cancellationToken)
    {
        var summary = await users.GetSummaryAsync(UserId.From(query.UserId), cancellationToken);
        if (summary is null)
        {
            return Result.Failure<UserSummary>(UserErrors.NotFound);
        }

        if (summary.TenantId is not { } tenantId)
        {
            return Result.Success(summary);
        }

        var timeZone = await tenancy.TimeZoneAsync(SchoolId.From(tenantId), cancellationToken);
        return Result.Success(summary with { SchoolTimeZone = timeZone });
    }
}

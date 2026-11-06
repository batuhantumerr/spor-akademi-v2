using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Identity.Profile;

public sealed record GetCurrentUserQuery(Guid UserId) : IQuery<UserSummary>;

internal sealed class GetCurrentUserHandler(IUserRepository users) : IQueryHandler<GetCurrentUserQuery, UserSummary>
{
    public async Task<Result<UserSummary>> Handle(GetCurrentUserQuery query, CancellationToken cancellationToken)
    {
        var summary = await users.GetSummaryAsync(UserId.From(query.UserId), cancellationToken);
        return summary is null ? Result.Failure<UserSummary>(UserErrors.NotFound) : Result.Success(summary);
    }
}

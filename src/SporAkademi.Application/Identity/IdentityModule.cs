using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Identity;

/// <summary>Opens accounts with a one-time temporary password (US-01, US-08).</summary>
internal sealed class AccountOpening(IUserRepository users, IPasswordHasher passwordHasher)
{
    public async Task<Result<(User User, string TemporaryPassword)>> OpenAsync(
        SchoolId schoolId,
        UserRole role,
        string firstName,
        string lastName,
        string email,
        PhoneNumber? phone,
        CancellationToken cancellationToken)
    {
        var address = Email.Create(email);
        if (address.IsFailure)
        {
            return Result.Failure<(User, string)>(address.Error);
        }

        if (await users.EmailExistsAsync(address.Value, cancellationToken))
        {
            return Result.Failure<(User, string)>(UserErrors.EmailTaken);
        }

        var temporaryPassword = TemporaryPassword.Generate();
        var created = User.Create(schoolId, address.Value, passwordHasher.Hash(temporaryPassword), role, firstName, lastName, phone);
        if (created.IsFailure)
        {
            return Result.Failure<(User, string)>(created.Error);
        }

        users.Add(created.Value);
        return Result.Success((created.Value, temporaryPassword));
    }
}

internal sealed class IdentityModule(IUserRepository users, AccountOpening accounts, IPasswordHasher passwordHasher, TimeProvider timeProvider) : IIdentityModule
{
    public async Task<Result<Guid>> InviteParentAsync(SchoolId schoolId, Guid? existingUserId, ParentAccountDetails details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(details);
        var address = Email.Create(details.Email);
        if (address.IsFailure)
        {
            return Result.Failure<Guid>(address.Error);
        }

        // DEC-378: the account never gets a password anyone knows; the invitation link sets the first one.
        var unusablePassword = passwordHasher.Hash(TemporaryPassword.Generate() + TemporaryPassword.Generate());
        if (existingUserId is not { } userId)
        {
            if (await users.EmailExistsAsync(address.Value, cancellationToken))
            {
                return Result.Failure<Guid>(UserErrors.EmailTaken);
            }

            var invited = User.InviteParent(schoolId, address.Value, unusablePassword, details.FirstName, details.LastName, details.Phone, timeProvider.GetUtcNow());
            if (invited.IsFailure)
            {
                return Result.Failure<Guid>(invited.Error);
            }

            users.Add(invited.Value);
            return Result.Success(invited.Value.Id.Value);
        }

        if (await users.GetParentAsync(schoolId, UserId.From(userId), cancellationToken) is not { } parent)
        {
            return Result.Failure<Guid>(UserErrors.NotFound);
        }

        if (parent.Email != address.Value && await users.EmailExistsAsync(address.Value, cancellationToken))
        {
            return Result.Failure<Guid>(UserErrors.EmailTaken);
        }

        var reinvited = parent.ReinviteParent(address.Value, unusablePassword, details.FirstName, details.LastName, details.Phone, timeProvider.GetUtcNow());
        return reinvited.IsSuccess ? Result.Success(userId) : Result.Failure<Guid>(reinvited.Error);
    }

    public async Task CloseParentAccountAsync(SchoolId schoolId, Guid userId, bool erasePersonalData, CancellationToken cancellationToken)
    {
        if (await users.GetParentAsync(schoolId, UserId.From(userId), cancellationToken) is not { } parent)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (erasePersonalData)
        {
            parent.EraseParent(now);
        }
        else if (parent.Status == UserStatus.Active)
        {
            _ = parent.Deactivate(now);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, ParentAccountInfo>> GetParentAccountsAsync(
        SchoolId schoolId,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, ParentAccountInfo>();
        }

        var parents = await users.ListParentsAsync(schoolId, [.. userIds.Distinct().Select(UserId.From)], cancellationToken);
        return parents.ToDictionary(p => p.Id.Value, ParentAccounts.InfoOf);
    }

    public async Task<Result<NewAccount>> CreateSchoolAdminAsync(SchoolId schoolId, string firstName, string lastName, string email, CancellationToken cancellationToken)
    {
        var opened = await accounts.OpenAsync(schoolId, UserRole.SchoolAdmin, firstName, lastName, email, null, cancellationToken);
        return opened.IsSuccess
            ? Result.Success(new NewAccount(opened.Value.User.Id.Value, opened.Value.TemporaryPassword))
            : Result.Failure<NewAccount>(opened.Error);
    }

    public Task<IReadOnlyDictionary<SchoolId, SchoolAdminSummary>> GetFirstSchoolAdminsAsync(IReadOnlyCollection<SchoolId> schoolIds, CancellationToken cancellationToken) =>
        users.GetFirstSchoolAdminsAsync(schoolIds, cancellationToken);

    public Task<IReadOnlySet<Guid>> GetActiveCoachIdsAsync(SchoolId schoolId, IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken) =>
        users.GetActiveCoachIdsAsync(schoolId, coachIds, cancellationToken);
}

internal static class ParentAccounts
{
    /// <summary>DEC-378: the status follows from the account; a closed account is "revoked" whatever closed it.</summary>
    public static ParentAccountInfo InfoOf(User parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var status = parent.Status == UserStatus.Deactivated
            ? ParentAccountStatus.Revoked
            : parent.MustChangePassword ? ParentAccountStatus.Invited : ParentAccountStatus.Active;
        return new ParentAccountInfo(parent.Id.Value, status, status == ParentAccountStatus.Invited ? parent.InvitationExpiresAt : null);
    }
}

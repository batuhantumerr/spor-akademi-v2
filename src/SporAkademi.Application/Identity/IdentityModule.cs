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

internal sealed class IdentityModule(IUserRepository users, AccountOpening accounts) : IIdentityModule
{
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

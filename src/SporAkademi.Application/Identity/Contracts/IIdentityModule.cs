using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Identity.Contracts;

/// <summary>A new account and its temporary password, which is shown once and never stored in clear.</summary>
public sealed record NewAccount(Guid UserId, string TemporaryPassword);

public sealed record SchoolAdminSummary(Guid Id, string FirstName, string LastName, string Email, UserStatus Status);

/// <summary>What other modules may ask the Identity module (ADR-0009: modules talk through contracts).</summary>
public interface IIdentityModule
{
    /// <summary>
    /// US-01: opens the first School Admin account of a new school with a temporary password. The one
    /// synchronous cross-module command: it joins the caller's transaction, so school and admin are
    /// created together or not at all. Fails with <c>User.EmailTaken</c>.
    /// </summary>
    Task<Result<NewAccount>> CreateSchoolAdminAsync(SchoolId schoolId, string firstName, string lastName, string email, CancellationToken cancellationToken);

    /// <summary>The first School Admin of each given school, for the platform's school list.</summary>
    Task<IReadOnlyDictionary<SchoolId, SchoolAdminSummary>> GetFirstSchoolAdminsAsync(IReadOnlyCollection<SchoolId> schoolIds, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="coachIds"/> are active coaches of <paramref name="schoolId"/> (US-08: a group
    /// whose coach was deactivated shows "no active coach"). Unknown ids and other schools' users are left out.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetActiveCoachIdsAsync(SchoolId schoolId, IReadOnlyCollection<Guid> coachIds, CancellationToken cancellationToken);
}

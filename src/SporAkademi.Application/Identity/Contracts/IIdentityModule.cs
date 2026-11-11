using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.Identity.Contracts;

/// <summary>A new account and its temporary password, which is shown once and never stored in clear.</summary>
public sealed record NewAccount(Guid UserId, string TemporaryPassword);

public sealed record SchoolAdminSummary(Guid Id, string FirstName, string LastName, string Email, UserStatus Status);

/// <summary>Where a guardian's parent account stands (P3-02, DEC-382), as the School Admin sees it.</summary>
public enum ParentAccountStatus
{
    /// <summary>"Davet edilmedi": the guardian has no account.</summary>
    NotInvited,

    /// <summary>"Davet gönderildi": invited; the parent has not set a password yet.</summary>
    Invited,

    /// <summary>"Aktif": the parent set a password and can sign in.</summary>
    Active,

    /// <summary>"İptal": revoked by the School Admin, or closed because the guardian has no active student (DEC-383).</summary>
    Revoked,
}

/// <param name="InvitationExpiresAt">When the pending invitation link expires; null when no invitation link is pending.</param>
public sealed record ParentAccountInfo(Guid UserId, ParentAccountStatus Status, DateTimeOffset? InvitationExpiresAt);

/// <summary>Who a parent account is for: the guardian's name, phone and e-mail (the login).</summary>
public sealed record ParentAccountDetails(string FirstName, string LastName, string Email, PhoneNumber? Phone);

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

    /// <summary>
    /// P3-02 (DEC-382): invites a guardian to a parent account of <paramref name="schoolId"/>, joining the caller's
    /// transaction. Without <paramref name="existingUserId"/> a new account is opened; with it the account is invited
    /// again (a pending invitation is resent, a revoked or closed one reopened; every earlier link stops working at
    /// once). The invitation e-mail goes out after commit. Fails with <c>User.EmailTaken</c> when another account uses
    /// the address, and with <c>ParentAccount.AlreadyActive</c> when the parent already set a password.
    /// </summary>
    /// <returns>The account's user id.</returns>
    Task<Result<Guid>> InviteParentAsync(SchoolId schoolId, Guid? existingUserId, ParentAccountDetails details, CancellationToken cancellationToken);

    /// <summary>
    /// P3-02 (DEC-383): closes a parent account of <paramref name="schoolId"/> inside the caller's transaction: it is
    /// deactivated, its sessions (refresh tokens) are revoked and a pending link stops working. An account that is
    /// already closed is left closed; an unknown id or another role or school is ignored. With
    /// <paramref name="erasePersonalData"/> (the guardian itself was deleted) the account's name, phone and e-mail are
    /// replaced by placeholders as well, even when it was already closed.
    /// </summary>
    Task CloseParentAccountAsync(SchoolId schoolId, Guid userId, bool erasePersonalData, CancellationToken cancellationToken);

    /// <summary>The status of the given parent accounts of <paramref name="schoolId"/>; other ids are left out.</summary>
    Task<IReadOnlyDictionary<Guid, ParentAccountInfo>> GetParentAccountsAsync(SchoolId schoolId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

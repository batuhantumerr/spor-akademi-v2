using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity.Contracts;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students;

/// <summary>A guardian's parent account as the School Admin sees it (P3-02, DEC-383).</summary>
/// <param name="InvitationExpiresAt">When the pending invitation link expires; null until the e-mail is sent, and when no link is pending.</param>
public sealed record GuardianInvitationDto(Guid GuardianId, ParentAccountStatus Status, DateTimeOffset? InvitationExpiresAt);

/// <summary>
/// P3-02: invites the guardian to a parent account, or sends the invitation again. A pending invitation's earlier link
/// stops working at once; a revoked or closed account is reopened (DEC-383).
/// </summary>
public sealed record InviteGuardianCommand(GuardianId Id) : ICommand<Unit>;

/// <summary>P3-02: revokes the invitation or closes the parent account: sessions end and a pending link stops working.</summary>
public sealed record RevokeGuardianInvitationCommand(GuardianId Id) : ICommand<Unit>;

public sealed record GetGuardianInvitationQuery(GuardianId Id) : IQuery<GuardianInvitationDto>;

/// <summary>P3-03: the signed-in parent's children (DEC-385).</summary>
public sealed record ListMyChildrenQuery : IQuery<IReadOnlyList<ParentChildDto>>;

/// <summary>A child as the parent app shows it (DEC-385): no emergency contact, documents or other guardians.</summary>
public sealed record ParentChildDto(Guid Id, string FirstName, string LastName, DateOnly? BirthDate, Gender Gender, GuardianRelationship Relationship);

internal sealed class InviteGuardianHandler(
    IGuardianRepository guardians,
    IIdentityModule identity,
    ICurrentUser currentUser) : ICommandHandler<InviteGuardianCommand, Unit>
{
    public async Task<Result<Unit>> Handle(InviteGuardianCommand command, CancellationToken cancellationToken)
    {
        // DEC-384: the same row lock as deleting an unlinked guardian, so an invitation and the guardian's deletion (or a
        // second invitation) never interleave; the guardian is read after the lock, so it is the committed state.
        await guardians.LockForDeletionAsync(command.Id, cancellationToken);
        var guardian = await guardians.GetAsync(command.Id, cancellationToken);
        if (guardian is null)
        {
            return Result.Failure<Unit>(GuardianErrors.NotFound);
        }

        if (guardian.Email is not { } email)
        {
            return Result.Failure<Unit>(GuardianErrors.EmailRequired);
        }

        if (!await guardians.HasActiveStudentAsync(guardian.Id, cancellationToken))
        {
            return Result.Failure<Unit>(GuardianErrors.NoActiveStudent);
        }

        var invited = await identity.InviteParentAsync(
            currentUser.RequireTenant(),
            guardian.AccountUserId,
            new ParentAccountDetails(guardian.FirstName, guardian.LastName, email.Value, guardian.Phone),
            cancellationToken);
        if (invited.IsFailure)
        {
            return Result.Failure<Unit>(invited.Error);
        }

        guardian.LinkAccount(invited.Value);
        return Result.Success(Unit.Value);
    }
}

internal sealed class RevokeGuardianInvitationHandler(
    IGuardianRepository guardians,
    IIdentityModule identity,
    ICurrentUser currentUser) : ICommandHandler<RevokeGuardianInvitationCommand, Unit>
{
    public async Task<Result<Unit>> Handle(RevokeGuardianInvitationCommand command, CancellationToken cancellationToken)
    {
        var guardian = await guardians.GetAsync(command.Id, cancellationToken);
        if (guardian is null)
        {
            return Result.Failure<Unit>(GuardianErrors.NotFound);
        }

        if (guardian.AccountUserId is not { } account)
        {
            return Result.Failure<Unit>(ParentAccountErrors.NotInvited);
        }

        // Revoking a closed account again changes nothing and is not an error: the admin's intent already holds.
        await identity.CloseParentAccountAsync(currentUser.RequireTenant(), account, erasePersonalData: false, cancellationToken);
        return Result.Success(Unit.Value);
    }
}

internal sealed class GetGuardianInvitationHandler(
    IGuardianRepository guardians,
    IIdentityModule identity,
    ICurrentUser currentUser) : IQueryHandler<GetGuardianInvitationQuery, GuardianInvitationDto>
{
    public async Task<Result<GuardianInvitationDto>> Handle(GetGuardianInvitationQuery query, CancellationToken cancellationToken)
    {
        if (!await guardians.ExistsAsync(query.Id, cancellationToken))
        {
            return Result.Failure<GuardianInvitationDto>(GuardianErrors.NotFound);
        }

        var invitations = await ParentInvitations.OfAsync(guardians, identity, currentUser, [query.Id], cancellationToken);
        return Result.Success(invitations[query.Id]);
    }
}

internal static class ParentInvitations
{
    /// <summary>The invitation status of each guardian (all of them present in the result).</summary>
    public static async Task<IReadOnlyDictionary<GuardianId, GuardianInvitationDto>> OfAsync(
        IGuardianRepository guardians,
        IIdentityModule identity,
        ICurrentUser currentUser,
        IReadOnlyCollection<GuardianId> ids,
        CancellationToken cancellationToken)
    {
        var accountOf = await guardians.AccountUserIdsAsync(ids, cancellationToken);
        var accounts = accountOf.Count == 0
            ? new Dictionary<Guid, ParentAccountInfo>()
            : await identity.GetParentAccountsAsync(currentUser.RequireTenant(), [.. accountOf.Values], cancellationToken);
        return ids.Distinct().ToDictionary(
            id => id,
            id => accountOf.TryGetValue(id, out var user) && accounts.TryGetValue(user, out var info)
                ? new GuardianInvitationDto(id.Value, info.Status, info.InvitationExpiresAt)
                : new GuardianInvitationDto(id.Value, ParentAccountStatus.NotInvited, null));
    }
}

/// <summary>
/// DEC-365, DEC-368, DEC-384: what happens to guardians after their student links changed. A guardian no student is
/// linked to any more is deleted; a parent account whose guardian was deleted, or whose guardian has no active student
/// left, is closed. Runs inside the transaction of the change (domain event consumers), so the account can never
/// outlive the reason it existed for. Uses the guardian's school, never the caller's: the anonymization job runs it too.
/// </summary>
internal sealed class GuardianAftercare(IGuardianRepository guardians, IIdentityModule identity)
{
    /// <summary>
    /// Deletes those of <paramref name="candidates"/> that no student is linked to. Each guardian row is locked
    /// (<c>FOR UPDATE</c>) before the check, and linking takes <c>FOR SHARE</c> first (DEC-368), so a concurrent
    /// registration either is seen here or no longer finds the guardian. Runs after the caller's links were saved.
    /// The remaining candidates' parent accounts are closed when they have no active student.
    /// </summary>
    /// <returns>The number of deleted guardians.</returns>
    public async Task<int> AfterUnlinkAsync(IEnumerable<GuardianId> candidates, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var id in InLockOrder(candidates))
        {
            await guardians.LockForDeletionAsync(id, cancellationToken);
            if (await guardians.GetAsync(id, cancellationToken) is not { } guardian)
            {
                continue;
            }

            if (await guardians.IsLinkedToAnyStudentAsync(id, cancellationToken))
            {
                await CloseIfNoActiveStudentAsync(guardian, cancellationToken);
                continue;
            }

            // The guardian's personal data goes, so the account's copy of it goes too (DEC-384).
            if (guardian.AccountUserId is { } account)
            {
                await identity.CloseParentAccountAsync(guardian.TenantId, account, erasePersonalData: true, cancellationToken);
            }

            guardians.Remove(guardian);
            deleted++;
        }

        return deleted;
    }

    /// <summary>DEC-384: closes the parent accounts of these guardians that have no active student any more.</summary>
    public async Task AfterStudentLeftAsync(IEnumerable<GuardianId> candidates, CancellationToken cancellationToken)
    {
        foreach (var id in InLockOrder(candidates))
        {
            // The same row lock as an invitation, so a leave and an invitation never interleave (DEC-384).
            await guardians.LockForDeletionAsync(id, cancellationToken);
            if (await guardians.GetAsync(id, cancellationToken) is { } guardian)
            {
                await CloseIfNoActiveStudentAsync(guardian, cancellationToken);
            }
        }
    }

    /// <summary>Guardian rows are always locked in id order, so two transactions locking several never wait in a cycle.</summary>
    private static IEnumerable<GuardianId> InLockOrder(IEnumerable<GuardianId> candidates) =>
        candidates.Distinct().OrderBy(id => id.Value);

    private async Task CloseIfNoActiveStudentAsync(Guardian guardian, CancellationToken cancellationToken)
    {
        if (guardian.AccountUserId is { } account && !await guardians.HasActiveStudentAsync(guardian.Id, cancellationToken))
        {
            await identity.CloseParentAccountAsync(guardian.TenantId, account, erasePersonalData: false, cancellationToken);
        }
    }
}

/// <summary>DEC-384: when a student leaves, a guardian with no other active student loses parent access.</summary>
internal sealed class CloseParentAccountsWhenStudentLeaves(IGuardianRepository guardians, GuardianAftercare aftercare)
    : IDomainEventConsumer<StudentLeft>
{
    public async Task<Result> Handle(StudentLeft domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await aftercare.AfterStudentLeftAsync(await guardians.ListOfStudentAsync(domainEvent.StudentId, cancellationToken), cancellationToken);
        return Result.Success();
    }
}

internal sealed class ListMyChildrenHandler(IParentChildren children) : IQueryHandler<ListMyChildrenQuery, IReadOnlyList<ParentChildDto>>
{
    public async Task<Result<IReadOnlyList<ParentChildDto>>> Handle(ListMyChildrenQuery query, CancellationToken cancellationToken)
    {
        var mine = await children.ListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<ParentChildDto>>(
            [.. mine.Select(c => new ParentChildDto(c.StudentId, c.FirstName, c.LastName, c.BirthDate, c.Gender, c.Relationship))]);
    }
}

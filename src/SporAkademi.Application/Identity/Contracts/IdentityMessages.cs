using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Identity.Contracts;

/// <summary>
/// A new School Admin or Coach account was committed (US-01, US-08): the welcome e-mail with a set-password link goes
/// out (P2-F4, DEC-351). Ids only (NFR-PRV-03); the handler reads the address when it runs.
/// </summary>
[OutboxMessage("identity.account-opened.v1")]
public sealed record AccountOpenedMessage(Guid UserId) : IOutboxMessage;

/// <summary>
/// A self-service password reset was requested and accepted (P2-F4, DEC-353). The token is created when the e-mail is
/// sent, so it is never stored in clear, not even in the outbox.
/// </summary>
[OutboxMessage("identity.password-reset-requested.v1")]
public sealed record PasswordResetRequestedMessage(Guid UserId) : IOutboxMessage;

/// <summary>
/// A guardian was invited (or invited again) to a parent account (P3-02, DEC-383): the invitation e-mail with a 72-hour
/// set-password link goes out. Like the reset, the token is created when the e-mail is sent.
/// </summary>
[OutboxMessage("identity.parent-invited.v1")]
public sealed record ParentInvitedMessage(Guid UserId) : IOutboxMessage;

/// <summary>
/// The password of an account changed (P4-03, ASVS 2.2.3, DEC-512): the owner is told by e-mail. Ids only; the handler
/// reads the address when it runs, and the message names no password or link.
/// </summary>
[OutboxMessage("identity.password-changed.v1")]
public sealed record PasswordChangedMessage(Guid UserId) : IOutboxMessage;

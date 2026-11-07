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

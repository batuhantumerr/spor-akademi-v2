using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>
/// Integration message after an online payment's success is committed (P2-F1, DEC-342). Ids only (NFR-PRV-03); a
/// receipt e-mail (P2-F4) reads what it needs when it runs.
/// </summary>
[OutboxMessage("billing.online-payment-succeeded.v1")]
public sealed record OnlinePaymentSucceededMessage(Guid OnlinePaymentId, Guid PaymentId) : IOutboxMessage;

/// <summary>
/// A fee reminder was recorded (P2-F4, DEC-355); its e-mail to one recipient goes out after commit (one message per
/// recipient, so a retry never re-sends to the others, DEC-356). Ids only (NFR-PRV-03): <see cref="RecipientId"/> is the
/// guardian's or the adult student's id; the handler reads the payments and the recipients when it runs.
/// </summary>
[OutboxMessage("billing.dues-reminder.v1")]
public sealed record DuesReminderMessage(Guid ReminderId, Guid RecipientId) : IOutboxMessage;

/// <summary>
/// A payment was recorded, by hand (US-23) or online (P2-F1) (P3-08, DEC-397). Ids only (NFR-PRV-03): no amount, no
/// method; the parent app reads the child's dues and payments again.
/// </summary>
[OutboxMessage("billing.payment-recorded.v1")]
public sealed record PaymentRecordedMessage(Guid PaymentId, Guid StudentId, Guid GroupId) : IOutboxMessage;

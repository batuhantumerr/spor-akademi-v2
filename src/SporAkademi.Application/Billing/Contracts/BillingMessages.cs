using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>
/// A fee reminder was recorded (P2-F4, DEC-355); its e-mail to one recipient goes out after commit (one message per
/// recipient, so a retry never re-sends to the others, DEC-356). Ids only (NFR-PRV-03): <see cref="RecipientId"/> is the
/// guardian's or the adult student's id; the handler reads the payments and the recipients when it runs.
/// </summary>
[OutboxMessage("billing.dues-reminder.v1")]
public sealed record DuesReminderMessage(Guid ReminderId, Guid RecipientId) : IOutboxMessage;

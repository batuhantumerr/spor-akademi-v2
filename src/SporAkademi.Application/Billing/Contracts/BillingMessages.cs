using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>
/// A fee reminder was recorded (P2-F4, DEC-355); its e-mail goes out after commit. The id only (NFR-PRV-03): the
/// handler reads the payments and the recipients when it runs.
/// </summary>
[OutboxMessage("billing.dues-reminder.v1")]
public sealed record DuesReminderMessage(Guid ReminderId) : IOutboxMessage;

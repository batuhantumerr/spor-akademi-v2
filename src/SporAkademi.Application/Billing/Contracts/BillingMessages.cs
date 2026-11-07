using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Application.Billing.Contracts;

/// <summary>
/// Integration message after an online payment's success is committed (P2-F1, DEC-342). Ids only (NFR-PRV-03); a
/// receipt e-mail (P2-F4) reads what it needs when it runs.
/// </summary>
[OutboxMessage("billing.online-payment-succeeded.v1")]
public sealed record OnlinePaymentSucceededMessage(Guid OnlinePaymentId, Guid PaymentId) : IOutboxMessage;

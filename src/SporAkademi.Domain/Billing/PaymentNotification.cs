using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.Billing;

public readonly record struct PaymentNotificationId(Guid Value) : IEntityId<PaymentNotificationId>
{
    public static PaymentNotificationId From(Guid value) => new(value);
}

/// <summary>
/// A provider notification (webhook) that was applied (DEC-343). Its key is unique per provider, so a delivery the
/// provider repeats is recognised and acknowledged without being applied again. Stored in the same transaction as the
/// change it caused: a failed attempt leaves no row, and the provider's retry is applied normally.
/// </summary>
public sealed class PaymentNotification : AggregateRoot<PaymentNotificationId>, ITenantScoped
{
    public const int EventKeyMaxLength = 200;

    private PaymentNotification(PaymentNotificationId id, SchoolId tenantId, string provider, string eventKey, OnlinePaymentId onlinePaymentId, DateTimeOffset receivedAt)
        : base(id)
    {
        TenantId = tenantId;
        Provider = provider;
        EventKey = eventKey;
        OnlinePaymentId = onlinePaymentId;
        ReceivedAt = receivedAt;
    }

    // For EF Core materialization only.
    private PaymentNotification()
        : base(default)
    {
        Provider = null!;
        EventKey = null!;
    }

    public SchoolId TenantId { get; private set; }

    public string Provider { get; private set; }

    /// <summary>The provider's identity of the event (event type, payment id, status); never personal data.</summary>
    public string EventKey { get; private set; }

    public OnlinePaymentId OnlinePaymentId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static PaymentNotification Receive(OnlinePayment payment, string eventKey, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(eventKey.Length, EventKeyMaxLength, nameof(eventKey));
        return new PaymentNotification(EntityId.New<PaymentNotificationId>(), payment.TenantId, payment.Provider, eventKey, payment.Id, receivedAt);
    }
}

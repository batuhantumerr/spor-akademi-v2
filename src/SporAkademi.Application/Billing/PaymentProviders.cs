using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// A payment provider with a hosted checkout (P2-F1, DEC-340): the payer pays on the provider's page, the provider
/// redirects the browser to our callback and (optionally) sends a signed webhook. The provider's status query is the
/// only source of truth: callbacks and webhooks only say "look again".
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Stored with each online payment, e.g. <c>Fake</c> or <c>Iyzico</c>.</summary>
    string Name { get; }

    /// <summary>Opens a hosted checkout. Throws <see cref="PaymentProviderException"/> when the provider refuses or cannot be reached.</summary>
    Task<CheckoutOpening> OpenCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>The provider's current view of a checkout. Throws <see cref="PaymentProviderException"/> when it cannot answer.</summary>
    Task<ProviderPaymentState> RetrieveAsync(string token, CancellationToken cancellationToken);

    /// <summary>Verifies a webhook's signature and reads which checkout it is about; <c>null</c> when it is not authentic.</summary>
    ProviderNotification? VerifyNotification(string body, IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Returns the full amount of a charge (P3-13, DEC-418). A definite answer is returned (refunded, or refused with a
    /// code); when the outcome is unknown (unreachable, timeout, server error) it throws <see cref="PaymentProviderException"/>
    /// and the refund stays pending for the reconciliation job. Calling it again for the same refund must never return
    /// money twice: the refund is always the full amount, so a provider has nothing left to refund after a success.
    /// </summary>
    Task<ProviderRefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken);
}

/// <summary>What the provider needs to refund a charge: ids and the amount only, never the reason or personal data.</summary>
/// <param name="RefundId">Our refund id; sent as the provider's conversation id.</param>
public sealed record RefundRequest(Guid RefundId, string ProviderPaymentId, Money Amount);

/// <summary>A definite answer to a refund request.</summary>
/// <param name="ProviderRefundId">The provider's reference of the refund when <paramref name="Succeeded"/>.</param>
/// <param name="FailureCode">The provider's error code when it refused.</param>
public sealed record ProviderRefundResult(bool Succeeded, string? ProviderRefundId, string? FailureCode)
{
    public static ProviderRefundResult Refunded(string providerRefundId) => new(true, providerRefundId, null);

    public static ProviderRefundResult Declined(string failureCode) => new(false, null, failureCode);
}

/// <summary>What the provider needs to open a checkout: ids and the amount only, no personal data (DEC-345).</summary>
public sealed record CheckoutRequest(Guid OnlinePaymentId, Guid EnrollmentId, Guid StudentId, string Period, Money Amount);

public sealed record CheckoutOpening(string Token, string CheckoutUrl, DateTimeOffset ExpiresAt);

public enum ProviderPaymentOutcome
{
    /// <summary>Not paid (yet), in review, or not decidable; nothing changes.</summary>
    Pending,
    Succeeded,
    Failed,
}

/// <param name="ConversationId">Our online payment id as the provider echoes it; must match the payment found by token.</param>
public sealed record ProviderPaymentState(
    ProviderPaymentOutcome Outcome,
    Guid? ConversationId,
    string? ProviderPaymentId,
    Money? PaidAmount,
    string? FailureCode);

/// <param name="Token">The checkout token the notification is about.</param>
/// <param name="EventKey">The provider's identity of the event, unique per delivery content; the de-duplication key.</param>
public sealed record ProviderNotification(string Token, string EventKey);

/// <summary>The provider refused or could not be reached. The message is a provider error code, never personal data.</summary>
public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException()
    {
    }

    public PaymentProviderException(string code)
        : base(code)
    {
    }

    public PaymentProviderException(string code, Exception innerException)
        : base(code, innerException)
    {
    }
}

/// <summary>The configured provider (DEC-340); <c>null</c> when online payment is disabled (e.g. Production without iyzico keys).</summary>
public interface IPaymentProviders
{
    IPaymentProvider? Active { get; }

    /// <summary>How long an attempt may stay <c>Created</c> (no checkout yet) before a new attempt for the due may start.</summary>
    TimeSpan CheckoutLifetime { get; }
}

/// <summary>Finds the school and id of an online payment by the provider's token; used before any tenant is known (DEC-344).</summary>
public interface ICheckoutTenantResolver
{
    Task<CheckoutTarget?> FindAsync(string provider, string token, CancellationToken cancellationToken);
}

public sealed record CheckoutTarget(Guid TenantId, Guid OnlinePaymentId);

/// <summary>
/// Runs work for one school in a fresh DI scope, like a request of that school: tenant filters and the write guard
/// apply (DEC-344). For anonymous entry points that learned the school from a stored record, never from the caller.
/// </summary>
public interface ITenantWorkScope
{
    Task<T> RunAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work);
}

/// <summary>The Fake provider's hosted checkout (DEC-347); available only while the Fake provider is active.</summary>
public interface ISandboxCheckout
{
    bool IsAvailable { get; }

    /// <summary>Simulates the payer finishing the checkout; <c>false</c> for an unknown token.</summary>
    bool Complete(string token, bool succeed);

    /// <summary>The signed webhook the Fake provider would send for <paramref name="token"/>'s current state.</summary>
    (string Body, IReadOnlyDictionary<string, string> Headers)? Webhook(string token);
}

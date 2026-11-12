using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Billing;

/// <summary>
/// Deterministic in-process provider for development, tests and the demo (DEC-347). It mimics iyzico's hosted checkout:
/// tokens and charge ids derive from the online payment id, the "hosted page" is the admin's <c>/sandbox-odeme</c>
/// page, and webhooks have iyzico's shape and signature (with a non-secret key). State lives in memory, so it is lost on
/// restart; it never runs in Production.
/// </summary>
internal sealed class FakePaymentProvider(IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentProvider
{
    public const string TokenPrefix = "fake-";
    public const string EventType = "CHECKOUT_FORM_AUTH";

    private readonly ConcurrentDictionary<string, FakeCheckout> _checkouts = new(StringComparer.Ordinal);

    /// <summary>Refunded charges: charge id → the refund id that refunded it (a full refund happens once).</summary>
    private readonly ConcurrentDictionary<string, string> _refunds = new(StringComparer.Ordinal);
    private int _failures;
    private int _declines;
    private int _refundCalls;
    private int _lostAnswers;
    private int _refundChecks;

    public string Name => PaymentOptions.FakeName;

    private string SigningKey => options.Value.Fake.SigningKey;

    /// <summary>Test hook: the next <paramref name="count"/> provider calls fail as if the provider were down.</summary>
    public void FailNextCalls(int count) => Interlocked.Exchange(ref _failures, count);

    public Task<CheckoutOpening> OpenCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfFailing();
        var token = TokenPrefix + request.OnlinePaymentId.ToString("N");
        var expiresAt = clock.GetUtcNow() + options.Value.CheckoutLifetime;
        _checkouts.TryAdd(token, new FakeCheckout(request.OnlinePaymentId, request.Amount, FakeState.Pending));
        return Task.FromResult(new CheckoutOpening(token, $"/sandbox-odeme?token={token}", expiresAt));
    }

    public Task<ProviderPaymentState> RetrieveAsync(string token, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (!_checkouts.TryGetValue(token, out var checkout))
        {
            throw new PaymentProviderException("FakeUnknownToken");
        }

        return Task.FromResult(checkout.State switch
        {
            FakeState.Succeeded => new ProviderPaymentState(ProviderPaymentOutcome.Succeeded, checkout.ConversationId, ChargeId(checkout), checkout.ReportedAmount ?? checkout.Amount, null),
            FakeState.Failed => new ProviderPaymentState(ProviderPaymentOutcome.Failed, checkout.ConversationId, null, null, "FakeDeclined"),
            _ => new ProviderPaymentState(ProviderPaymentOutcome.Pending, checkout.ConversationId, null, null, null),
        });
    }

    public ProviderNotification? VerifyNotification(string body, IReadOnlyDictionary<string, string> headers) =>
        IyzicoProtocol.VerifyWebhook(SigningKey, body, headers) is { } webhook ? new ProviderNotification(webhook.Token, webhook.EventKey) : null;

    /// <summary>The payer finished the checkout. A finished checkout stays as it is.</summary>
    public bool Complete(string token, bool succeed)
    {
        if (!_checkouts.TryGetValue(token, out var checkout))
        {
            return false;
        }

        if (checkout.State == FakeState.Pending)
        {
            _checkouts.TryUpdate(token, checkout with { State = succeed ? FakeState.Succeeded : FakeState.Failed }, checkout);
        }

        return true;
    }

    /// <summary>Test hook: the status query reports <paramref name="amount"/> as charged (a provider that disagrees with us).</summary>
    public bool ReportPaidAmount(string token, Money amount) =>
        _checkouts.TryGetValue(token, out var checkout) && _checkouts.TryUpdate(token, checkout with { ReportedAmount = amount }, checkout);

    /// <summary>The signed webhook iyzico would send for the checkout's current state.</summary>
    public (string Body, IReadOnlyDictionary<string, string> Headers)? Webhook(string token)
    {
        if (!_checkouts.TryGetValue(token, out var checkout) || checkout.State == FakeState.Pending)
        {
            return null;
        }

        var webhook = new HostedWebhook(
            EventType,
            ChargeId(checkout),
            token,
            checkout.ConversationId.ToString(),
            checkout.State == FakeState.Succeeded ? "SUCCESS" : "FAILURE");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [IyzicoProtocol.SignatureHeader] = IyzicoProtocol.WebhookSignature(SigningKey, webhook),
        };
        return (webhook.ToJson(), headers);
    }

    /// <summary>
    /// P3-13 (DEC-418, DEC-424): refunds a charge in full, with iyzico's semantics: a charge can be refunded once, and any
    /// later refund of it (even with the same refund id) is refused as "already refunded", which asks the caller to check
    /// the payment detail. An unknown charge id or a different amount is refused. Test hooks: <see cref="FailNextCalls"/>
    /// (unknown outcome, nothing refunded), <see cref="DeclineNextRefunds"/> (definite refusal) and
    /// <see cref="LoseNextRefundAnswers"/> (the money is returned but the answer is lost).
    /// </summary>
    public Task<ProviderRefundResult> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfFailing();
        if (TryTake(ref _declines))
        {
            return Task.FromResult(ProviderRefundResult.Declined("FakeRefundDeclined"));
        }

        var charge = _checkouts.Values.FirstOrDefault(c => c.State == FakeState.Succeeded && ChargeId(c) == request.ProviderPaymentId);
        if (!request.ProviderPaymentId.StartsWith("fp-", StringComparison.Ordinal) || (charge is not null && charge.Amount != request.Amount))
        {
            return Task.FromResult(ProviderRefundResult.Declined("FakeUnknownCharge"));
        }

        Interlocked.Increment(ref _refundCalls);
        var refundId = "fr-" + request.RefundId.ToString("N")[..16];
        if (!_refunds.TryAdd(request.ProviderPaymentId, refundId))
        {
            return Task.FromResult(ProviderRefundResult.Verify("FakeAlreadyRefunded"));
        }

        return TryTake(ref _lostAnswers)
            ? throw new PaymentProviderException("FakeAnswerLost")
            : Task.FromResult(ProviderRefundResult.Refunded(refundId));
    }

    /// <summary>DEC-418: the payment detail: the charge's money is back when it was refunded.</summary>
    public Task<ProviderRefundCheck> CheckRefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfFailing();
        Interlocked.Increment(ref _refundChecks);
        return Task.FromResult(_refunds.TryGetValue(request.ProviderPaymentId, out var refundId)
            ? new ProviderRefundCheck(true, refundId)
            : new ProviderRefundCheck(false, null));
    }

    /// <summary>Test hook: the next <paramref name="count"/> refunds return the money, but their answer is lost (a timeout).</summary>
    public void LoseNextRefundAnswers(int count) => Interlocked.Exchange(ref _lostAnswers, count);

    /// <summary>Test hook: how many payment detail checks reached the provider.</summary>
    public int RefundChecks => Volatile.Read(ref _refundChecks);

    /// <summary>Test hook: the next <paramref name="count"/> refunds are refused by the provider (a definite answer).</summary>
    public void DeclineNextRefunds(int count) => Interlocked.Exchange(ref _declines, count);

    /// <summary>Test hook: how many refund requests reached the Fake provider's ledger.</summary>
    public int RefundCalls => Volatile.Read(ref _refundCalls);

    /// <summary>Test hook: whether the charge was refunded at the provider.</summary>
    public bool IsRefunded(string providerPaymentId) => _refunds.ContainsKey(providerPaymentId);

    private static string ChargeId(FakeCheckout checkout) => "fp-" + checkout.ConversationId.ToString("N")[..16];

    private static bool TryTake(ref int counter)
    {
        while (true)
        {
            var remaining = Volatile.Read(ref counter);
            if (remaining <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, remaining - 1, remaining) == remaining)
            {
                return true;
            }
        }
    }

    private void ThrowIfFailing()
    {
        if (TryTake(ref _failures))
        {
            throw new PaymentProviderException("FakeUnavailable");
        }
    }

    private enum FakeState
    {
        Pending,
        Succeeded,
        Failed,
    }

    private sealed record FakeCheckout(Guid ConversationId, Money Amount, FakeState State, Money? ReportedAmount = null);
}

using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.Billing;

namespace SporAkademi.Application.Billing;

/// <summary>
/// The safety net for lost or never-sent webhooks (DEC-343): in the current school's scope, open attempts older than a
/// threshold are re-queried at the provider and their result applied (without a notification key), and open attempts
/// whose checkout lifetime passed become <c>Expired</c>. Run per school by the recurring reconciliation job.
/// </summary>
public sealed partial class OnlinePaymentReconciliation(
    IOnlinePaymentRepository onlinePayments,
    IPaymentProviders providers,
    ICommandHandler<ApplyProviderStateCommand, AppliedProviderState> apply,
    ICommandHandler<ExpireOnlinePaymentCommand, bool> expire,
    TimeProvider clock,
    ILogger<OnlinePaymentReconciliation> logger)
{
    /// <summary>At most this many attempts per school and run; the rest wait for the next run.</summary>
    public const int MaxPerRun = 100;

    /// <returns>How many attempts changed (credited, failed or expired).</returns>
    public async Task<int> ReconcileAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        var open = await onlinePayments.ListOpenStartedBeforeAsync(clock.GetUtcNow() - olderThan, MaxPerRun, cancellationToken);
        var changed = 0;
        foreach (var payment in open)
        {
            var settled = await ApplyProviderResultAsync(payment, cancellationToken);
            if (settled is { } applied)
            {
                changed += applied ? 1 : 0;
                continue;
            }

            var expired = await expire.Handle(new ExpireOnlinePaymentCommand(payment.Id), cancellationToken);
            changed += expired.IsSuccess && expired.Value ? 1 : 0;
        }

        return changed;
    }

    /// <returns><c>null</c> when the attempt is still open (or the provider could not be asked); otherwise whether it changed.</returns>
    private async Task<bool?> ApplyProviderResultAsync(OnlinePayment payment, CancellationToken cancellationToken)
    {
        if (providers.Active is not { } provider || payment.ProviderToken is not { } token
            || !string.Equals(provider.Name, payment.Provider, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var state = await provider.RetrieveAsync(token, cancellationToken);
            if (state.ConversationId != payment.Id.Value || state.Outcome == ProviderPaymentOutcome.Pending)
            {
                return null;
            }

            var applied = await apply.Handle(new ApplyProviderStateCommand(payment.Id, provider.Name, state, null), cancellationToken);
            return applied.IsSuccess ? applied.Value.Changed : null;
        }
        catch (PaymentProviderException exception)
        {
            LogRetrieveFailed(logger, payment.Id.Value, provider.Name, exception.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling online payment {OnlinePaymentId} with {Provider} failed with {ProviderError}")]
    private static partial void LogRetrieveFailed(ILogger logger, Guid onlinePaymentId, string provider, string providerError);
}

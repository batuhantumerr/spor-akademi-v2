using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Application.Billing;

/// <summary>
/// Starting an online payment (DEC-341): the attempt is committed first, then the provider is called outside any
/// database transaction or lock, then its answer is stored. A replay of a key whose attempt never got a checkout (e.g.
/// a crash in between) opens it now, so retrying with the same key always converges.
/// </summary>
public sealed partial class OnlineCheckout(
    ICommandHandler<StartOnlinePaymentCommand, StartedOnlinePayment> start,
    ICommandHandler<OpenOnlineCheckoutCommand, Unit> open,
    IOnlinePaymentRepository onlinePayments,
    IPaymentProviders providers,
    ILogger<OnlineCheckout> logger)
{
    public async Task<Result<StartedOnlinePayment>> StartAsync(Guid enrollmentId, string period, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var command = new StartOnlinePaymentCommand(enrollmentId, period, idempotencyKey);
        var started = await start.Handle(command, cancellationToken);
        if (started.IsFailure && started.Error.Code == IdempotencyErrors.InProgress.Code)
        {
            // A concurrent request with the same key won the insert and has committed: this is now a replay.
            started = await start.Handle(command, cancellationToken);
        }

        if (started.IsFailure)
        {
            return started;
        }

        var payment = await onlinePayments.FindAsync(started.Value.Id, cancellationToken);
        if (payment is not { Status: OnlinePaymentStatus.Created } || providers.Active is not { } provider
            || !string.Equals(provider.Name, payment.Provider, StringComparison.Ordinal))
        {
            return started;
        }

        CheckoutOpening opening;
        try
        {
            opening = await provider.OpenCheckoutAsync(
                new CheckoutRequest(payment.Id.Value, payment.EnrollmentId.Value, payment.StudentId.Value, payment.Period.ToString(), payment.Amount),
                cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            LogOpenFailed(logger, payment.Id.Value, provider.Name, exception.Message);
            await open.Handle(new OpenOnlineCheckoutCommand(payment.Id, null, exception.Message), CancellationToken.None);
            return Result.Failure<StartedOnlinePayment>(OnlinePaymentErrors.ProviderUnavailable);
        }

        var stored = await open.Handle(new OpenOnlineCheckoutCommand(payment.Id, opening, null), CancellationToken.None);
        // NotOpen or a concurrency conflict: a concurrent replay opened it first; its checkout is the one kept.
        return stored.IsSuccess || stored.Error.Code == OnlinePaymentErrors.NotOpen.Code || stored.Error.Code == PersistenceErrors.ConcurrencyConflict.Code
            ? started
            : Result.Failure<StartedOnlinePayment>(stored.Error);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the {Provider} checkout of online payment {OnlinePaymentId} failed with {ProviderError}")]
    private static partial void LogOpenFailed(ILogger logger, Guid onlinePaymentId, string provider, string providerError);
}

public enum NotificationOutcome
{
    /// <summary>The state changed: a payment was recorded or the attempt failed.</summary>
    Applied,

    /// <summary>Already applied (a repeated or concurrent delivery) or nothing to change; acknowledged.</summary>
    Unchanged,

    /// <summary>The provider does not report a final result yet.</summary>
    Pending,

    NotFound,
    InvalidSignature,

    /// <summary>The provider could not be asked; the sender should retry.</summary>
    ProviderUnavailable,

    /// <summary>The provider's report contradicts the stored attempt (other id or amount); not applied.</summary>
    Rejected,
    Disabled,
}

public sealed record NotificationResult(NotificationOutcome Outcome, OnlinePaymentStatus? Status = null);

/// <summary>
/// Anonymous entry points of the provider (DEC-343, DEC-344): the signed webhook and the browser's return from the
/// hosted checkout. The school comes only from the stored attempt found by the provider's token; the result comes only
/// from the provider's status query. Each delivery is applied at most once.
/// </summary>
public sealed partial class OnlinePaymentNotifications(
    IPaymentProviders providers,
    ICheckoutTenantResolver resolver,
    ITenantWorkScope tenantScope,
    ILogger<OnlinePaymentNotifications> logger)
{
    private const int MaxAttempts = 3;

    public Task<NotificationResult> HandleWebhookAsync(string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (providers.Active is not { } provider)
        {
            return Task.FromResult(new NotificationResult(NotificationOutcome.Disabled));
        }

        var notification = provider.VerifyNotification(body, headers);
        if (notification is null)
        {
            LogInvalidSignature(logger, provider.Name);
            return Task.FromResult(Counted(new NotificationResult(NotificationOutcome.InvalidSignature)));
        }

        return ReconcileAsync(provider, notification.Token, notification.EventKey, cancellationToken);
    }

    /// <summary>P4-07 (DEC-552): every answer to a notification is counted by outcome and resulting status (fixed values only).</summary>
    private static NotificationResult Counted(NotificationResult result)
    {
        AppTelemetry.PaymentNotifications.Add(
            1,
            new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()),
            new KeyValuePair<string, object?>("status", result.Status?.ToString() ?? "none"));
        return result;
    }

    /// <summary>The browser returned from the checkout with its token; anyone can send this, so it only triggers a status query.</summary>
    public Task<NotificationResult> HandleReturnAsync(string token, CancellationToken cancellationToken) =>
        providers.Active is { } provider
            ? ReconcileAsync(provider, token, eventKey: null, cancellationToken)
            : Task.FromResult(new NotificationResult(NotificationOutcome.Disabled));

    private async Task<NotificationResult> ReconcileAsync(IPaymentProvider provider, string token, string? eventKey, CancellationToken cancellationToken) =>
        Counted(await ReconcileOnceAsync(provider, token, eventKey, cancellationToken));

    private async Task<NotificationResult> ReconcileOnceAsync(IPaymentProvider provider, string token, string? eventKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > OnlinePayment.ProviderReferenceMaxLength)
        {
            return new NotificationResult(NotificationOutcome.NotFound);
        }

        var target = await resolver.FindAsync(provider.Name, token, cancellationToken);
        if (target is null)
        {
            return new NotificationResult(NotificationOutcome.NotFound);
        }

        ProviderPaymentState state;
        try
        {
            state = await provider.RetrieveAsync(token, cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            LogRetrieveFailed(logger, target.OnlinePaymentId, provider.Name, exception.Message);
            return new NotificationResult(NotificationOutcome.ProviderUnavailable);
        }

        if (state.ConversationId != target.OnlinePaymentId)
        {
            LogRejected(logger, target.OnlinePaymentId, provider.Name, "ConversationMismatch");
            return new NotificationResult(NotificationOutcome.Rejected);
        }

        var command = new ApplyProviderStateCommand(OnlinePaymentId.From(target.OnlinePaymentId), provider.Name, state, eventKey);
        for (var attempt = 1; ; attempt++)
        {
            var applied = await tenantScope.RunAsync(target.TenantId, services =>
                services.GetRequiredService<ICommandHandler<ApplyProviderStateCommand, AppliedProviderState>>().Handle(command, cancellationToken));

            if (applied.IsSuccess)
            {
                var outcome = applied.Value.Changed
                    ? NotificationOutcome.Applied
                    : state.Outcome == ProviderPaymentOutcome.Pending ? NotificationOutcome.Pending : NotificationOutcome.Unchanged;
                LogApplied(logger, target.OnlinePaymentId, provider.Name, outcome, applied.Value.Status);
                return new NotificationResult(outcome, applied.Value.Status);
            }

            // A concurrent delivery changed the row first (xmin, unique keys); the next attempt sees its result.
            if (applied.Error.Type == ErrorType.Conflict && attempt < MaxAttempts)
            {
                continue;
            }

            if (applied.Error.Code == OnlinePaymentErrors.NotFound.Code)
            {
                return new NotificationResult(NotificationOutcome.NotFound);
            }

            if (applied.Error.Type == ErrorType.Conflict)
            {
                return new NotificationResult(NotificationOutcome.ProviderUnavailable);
            }

            LogRejected(logger, target.OnlinePaymentId, provider.Name, applied.Error.Code);
            return new NotificationResult(NotificationOutcome.Rejected);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Provider} payment notification had an invalid signature")]
    private static partial void LogInvalidSignature(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Querying {Provider} for online payment {OnlinePaymentId} failed with {ProviderError}")]
    private static partial void LogRetrieveFailed(ILogger logger, Guid onlinePaymentId, string provider, string providerError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Provider} report for online payment {OnlinePaymentId} was rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, Guid onlinePaymentId, string provider, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Provider} result for online payment {OnlinePaymentId}: {Outcome}, status {Status}")]
    private static partial void LogApplied(ILogger logger, Guid onlinePaymentId, string provider, NotificationOutcome outcome, OnlinePaymentStatus status);
}

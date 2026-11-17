using Sentry;
using Sentry.Extensibility;

namespace SporAkademi.Infrastructure.Observability;

/// <summary>
/// P4-07 (DEC-556), shared with the notifications worker (P5-04, DEC-634): what leaves a process for Sentry is minimal. No
/// default PII, no breadcrumbs, no Sentry tracing, and exception messages are replaced by their type (they may quote personal
/// data, DEC-333). Both hosts apply the same code, so the worker can never send more than the API. Each host adds only what is
/// specific to it (the API's request body setting).
/// </summary>
public static class SentryPrivacy
{
    public const string Redacted = "[REDACTED]";

    /// <summary>The settings no configuration can turn back on.</summary>
    public static void Apply(SentryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // An empty DSN disables the SDK (null would make it fail at start, or fall back to SENTRY_DSN).
        if (string.IsNullOrWhiteSpace(options.Dsn))
        {
            options.Dsn = string.Empty;
        }

        options.SendDefaultPii = false;
        options.MaxBreadcrumbs = 0;
        options.SetBeforeBreadcrumb(_ => null);
        options.CaptureFailedRequests = false;
        options.TracesSampleRate = 0;
        options.AutoSessionTracking = false;
        options.SendClientReports = false;
        options.AttachStacktrace = true;
        options.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
    }

    /// <summary>
    /// The DSN comes from <c>Sentry:Dsn</c> only. The SDK replaces an empty DSN with <c>SENTRY_DSN</c> from the environment, a
    /// second, easily forgotten switch; without <c>Sentry:Dsn</c> every event is therefore dropped before sending and the
    /// transport discards anything else.
    /// </summary>
    public static void ApplyDsn(SentryOptions options, string? dsn)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(dsn))
        {
            options.Dsn = dsn;
            return;
        }

        options.Dsn = string.Empty;
        options.SetBeforeSend((_, _) => null);
        options.SetBeforeSendTransaction((_, _) => null);
        options.Transport = new DiscardingTransport();
    }

    /// <summary>Sends nothing anywhere.</summary>
    private sealed class DiscardingTransport : ITransport
    {
        public Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Keeps what identifies the fault (exception types, stack, route, release, trace id) and removes the rest.</summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);
        var request = sentryEvent.Request;
        sentryEvent.Request = new SentryRequest
        {
            Method = request.Method,
            Url = request.Url is { } url ? url.Split('?', '#')[0] : null,
        };
        sentryEvent.User = new SentryUser();
        sentryEvent.ServerName = null;

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = exception.Type ?? Redacted;
            exception.Mechanism?.Data.Clear();
        }

        if (sentryEvent.Message is { } message)
        {
            // A logged event keeps its template only (its values could be anything).
            sentryEvent.Message = new SentryMessage { Message = message.Message ?? Redacted };
        }

        foreach (var key in sentryEvent.Extra.Keys.ToList())
        {
            sentryEvent.SetExtra(key, Redacted);
        }

        return sentryEvent;
    }
}

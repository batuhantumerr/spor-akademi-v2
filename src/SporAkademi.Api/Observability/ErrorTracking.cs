using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace SporAkademi.Api.Observability;

/// <summary>
/// P4-07 (DEC-556): unexpected server errors go to Sentry when <c>Sentry:Dsn</c> is configured; without it the SDK is
/// disabled and sends nothing. The DSN is environment configuration only (the Deploy block sets it; no key in the
/// repository). Traces stay with OpenTelemetry, so Sentry's own performance monitoring is off. What leaves the process is
/// minimal: no default PII, no request body, cookies, headers or query string, no user, no breadcrumbs, and exception
/// messages are replaced by their type (they may quote personal data, DEC-333); the code-set settings below cannot be
/// turned back on from configuration.
/// </summary>
internal static class ErrorTracking
{
    public const string Redacted = "[REDACTED]";

    public static WebApplicationBuilder AddErrorTracking(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(Configure);
        // The DSN comes from Sentry:Dsn only (read from the final configuration, so test hosts can set it). The SDK replaces
        // an empty DSN with SENTRY_DSN from the environment, a second, easily forgotten switch; without Sentry:Dsn every
        // event is therefore dropped before sending and the transport discards anything else.
        builder.Services.AddOptions<SentryAspNetCoreOptions>().PostConfigure<IConfiguration>((options, configuration) =>
        {
            var dsn = configuration["Sentry:Dsn"];
            if (!string.IsNullOrWhiteSpace(dsn))
            {
                options.Dsn = dsn;
                return;
            }

            options.Dsn = string.Empty;
            options.SetBeforeSend((_, _) => null);
            options.SetBeforeSendTransaction((_, _) => null);
            options.Transport = new DiscardingTransport();
        });
        return builder;
    }

    /// <summary>Sends nothing anywhere.</summary>
    private sealed class DiscardingTransport : ITransport
    {
        public Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public static void Configure(SentryAspNetCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // An empty DSN disables the SDK (null would make it fail at start, or fall back to SENTRY_DSN).
        if (string.IsNullOrWhiteSpace(options.Dsn))
        {
            options.Dsn = string.Empty;
        }

        options.SendDefaultPii = false;
        options.MaxRequestBodySize = RequestSize.None;
        options.MaxBreadcrumbs = 0;
        options.SetBeforeBreadcrumb(_ => null);
        options.CaptureFailedRequests = false;
        options.TracesSampleRate = 0;
        options.AutoSessionTracking = false;
        options.SendClientReports = false;
        options.IncludeActivityData = false;
        options.AttachStacktrace = true;
        options.SetBeforeSend((sentryEvent, _) => Scrub(sentryEvent));
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

using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace SporAkademi.Notifications.Observability;

/// <summary>
/// P5-04 (DEC-634): unexpected errors go to Sentry when <c>Sentry:Dsn</c> is configured, with the API's privacy settings
/// (DEC-556): no PII, no breadcrumbs, no tracing, exception messages replaced by their type. Without a DSN the SDK sends nothing.
/// </summary>
internal static class WorkerErrorTracking
{
    public const string Redacted = "[REDACTED]";

    public static WebApplicationBuilder AddWorkerErrorTracking(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(Configure);
        builder.Services.AddOptions<SentryAspNetCoreOptions>().PostConfigure<IConfiguration>((options, configuration) =>
        {
            var dsn = configuration["Sentry:Dsn"];
            if (!string.IsNullOrWhiteSpace(dsn))
            {
                options.Dsn = dsn;
                return;
            }

            // The SDK would fall back to SENTRY_DSN from the environment; without Sentry:Dsn every event is dropped.
            options.Dsn = string.Empty;
            options.SetBeforeSend((_, _) => null);
            options.SetBeforeSendTransaction((_, _) => null);
            options.Transport = new DiscardingTransport();
        });
        return builder;
    }

    private sealed class DiscardingTransport : ITransport
    {
        public Task SendEnvelopeAsync(Sentry.Protocol.Envelopes.Envelope envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public static void Configure(SentryAspNetCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
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
            sentryEvent.Message = new SentryMessage { Message = message.Message ?? Redacted };
        }

        foreach (var key in sentryEvent.Extra.Keys.ToList())
        {
            sentryEvent.SetExtra(key, Redacted);
        }

        return sentryEvent;
    }
}

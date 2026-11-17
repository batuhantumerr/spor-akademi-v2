using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using SporAkademi.Infrastructure.Observability;

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
    public const string Redacted = SentryPrivacy.Redacted;

    public static WebApplicationBuilder AddErrorTracking(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(Configure);
        // The DSN comes from Sentry:Dsn only (read from the final configuration, so test hosts can set it).
        builder.Services.AddOptions<SentryAspNetCoreOptions>().PostConfigure<IConfiguration>((options, configuration) =>
            SentryPrivacy.ApplyDsn(options, configuration["Sentry:Dsn"]));
        return builder;
    }

    public static void Configure(SentryAspNetCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The settings shared with the notifications worker (P5-04): Infrastructure/Observability/SentryPrivacy.cs.
        SentryPrivacy.Apply(options);
        options.MaxRequestBodySize = RequestSize.None;
        options.IncludeActivityData = false;
    }

    public static SentryEvent Scrub(SentryEvent sentryEvent) => SentryPrivacy.Scrub(sentryEvent);
}

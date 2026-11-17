using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;
using SporAkademi.Infrastructure.Observability;

namespace SporAkademi.Notifications.Observability;

/// <summary>
/// P5-04 (DEC-634): unexpected errors go to Sentry when <c>Sentry:Dsn</c> is configured, with exactly the API's privacy settings:
/// the code is shared (<see cref="SentryPrivacy"/>, Infrastructure). Without a DSN the SDK sends nothing.
/// </summary>
internal static class WorkerErrorTracking
{
    public static WebApplicationBuilder AddWorkerErrorTracking(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseSentry(options =>
        {
            ArgumentNullException.ThrowIfNull(options);
            SentryPrivacy.Apply(options);
            options.MaxRequestBodySize = RequestSize.None;
            options.IncludeActivityData = false;
        });
        builder.Services.AddOptions<SentryAspNetCoreOptions>().PostConfigure<IConfiguration>((options, configuration) =>
            SentryPrivacy.ApplyDsn(options, configuration["Sentry:Dsn"]));
        return builder;
    }
}

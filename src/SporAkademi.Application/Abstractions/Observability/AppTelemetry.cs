using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SporAkademi.Application.Abstractions.Observability;

/// <summary>
/// P4-07 (DEC-551, DEC-552): the application's own spans and metrics, on the runtime's <see cref="ActivitySource"/> and
/// <see cref="Meter"/> (no OpenTelemetry dependency here; the API exports them when telemetry is on, otherwise nothing
/// listens and recording costs next to nothing). Attributes are fixed, low-cardinality values (codes, outcomes, message
/// types): never a school, user, e-mail or any other personal data, and no tenant id either, so one time series covers
/// all schools.
/// </summary>
public static class AppTelemetry
{
    public const string Name = "SporAkademi";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>Failed logins by error code (<c>reason</c>), e.g. a wrong password or a locked account.</summary>
    public static readonly Counter<long> LoginFailures = Meter.CreateCounter<long>(
        "spor_akademi.auth.login.failures", "{attempt}", "Failed login attempts by error code.");

    /// <summary>Outbox deliveries by <c>result</c> (delivered, retried, dead_lettered) and <c>message_type</c>.</summary>
    public static readonly Counter<long> OutboxMessages = Meter.CreateCounter<long>(
        "spor_akademi.outbox.messages", "{message}", "Outbox delivery attempts by result and message type.");

    /// <summary>Online payment notifications (webhook, browser return, reconcile) by <c>outcome</c> and resulting <c>status</c>.</summary>
    public static readonly Counter<long> PaymentNotifications = Meter.CreateCounter<long>(
        "spor_akademi.payments.online.notifications", "{notification}", "Online payment notifications by outcome and resulting status.");

    /// <summary>Real-time notices sent to SignalR groups, by <c>audience</c> (users or role).</summary>
    public static readonly Counter<long> RealtimeNotices = Meter.CreateCounter<long>(
        "spor_akademi.realtime.notices", "{notice}", "Real-time notices sent by audience.");

    /// <summary>Starts a span when anything listens, so spans and log events (TraceId) of background work belong together.</summary>
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) => Source.StartActivity(name, kind);
}

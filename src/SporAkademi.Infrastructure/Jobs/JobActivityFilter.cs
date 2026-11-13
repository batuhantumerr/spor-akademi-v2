using System.Diagnostics;
using Hangfire.Server;
using SporAkademi.Application.Abstractions.Observability;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// P4-07 (DEC-551): each Hangfire job run is a span (<c>job.run</c>, attribute <c>spor_akademi.job</c> = class and method),
/// so its database and HTTP calls are grouped under it. The outbox delivery job, which runs every 10 seconds and mostly
/// finds nothing, gets no span of its own: each delivered message has one (<c>outbox.deliver</c>). Hangfire calls the
/// filter on the thread that runs the job, so the span is the current one for the job's own work.
/// </summary>
internal sealed class JobActivityFilter : IServerFilter
{
    private const string ActivityKey = "spor-akademi.activity";

    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // A job runs on a long-lived worker thread: should an earlier job's OnPerformed never have run, its span must not
        // stay current and become this job's parent.
        Activity.Current = null;
        var job = context.BackgroundJob.Job;
        if (job.Type == typeof(OutboxJobs) && job.Method.Name == nameof(OutboxJobs.ProcessAsync))
        {
            return;
        }

        var activity = AppTelemetry.StartActivity("job.run");
        if (activity is null)
        {
            return;
        }

        activity.SetTag("spor_akademi.job", $"{job.Type.Name}.{job.Method.Name}");
        context.Items[ActivityKey] = activity;
    }

    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Items.TryGetValue(ActivityKey, out var item) || item is not Activity activity)
        {
            return;
        }

        if (context.Exception is { } exception)
        {
            // The type only: an exception message may quote personal data (DEC-333).
            activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }

        activity.Stop();
        activity.Dispose();
    }
}

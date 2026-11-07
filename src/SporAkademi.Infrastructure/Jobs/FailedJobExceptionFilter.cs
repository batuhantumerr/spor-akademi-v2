using Hangfire.States;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// Hangfire stores a failed job's exception (message and stack trace) in its schema and shows it in the panel. An
/// exception message may carry personal data (e.g. a database error quoting a value), so only the exception type is
/// kept (DEC-333); the failure is still visible and the type points at the cause.
/// </summary>
internal sealed class FailedJobExceptionFilter : IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CandidateState is FailedState { Exception: { } exception } failed and not { Exception: JobFailedException })
        {
            // The reason may quote the message too (e.g. retry texts), so Hangfire's default reason is used.
            context.CandidateState = new FailedState(new JobFailedException(exception.GetType().FullName ?? exception.GetType().Name), failed.ServerId);
        }
    }
}

/// <summary>Stands in for a job's exception in the stored failed state: its message is the original exception type only.</summary>
public sealed class JobFailedException : Exception
{
    public JobFailedException()
    {
    }

    public JobFailedException(string message)
        : base(message)
    {
    }

    public JobFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

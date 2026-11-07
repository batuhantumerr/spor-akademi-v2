namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Exponential backoff with "equal jitter": after the n-th failure the ceiling is
/// <c>min(base * 2^(n-1), max)</c> and the delay is uniformly between half the ceiling and the ceiling. The jitter
/// spreads retries of messages that failed together (e.g. during an outage); the lower bound keeps a minimum pause.
/// </summary>
internal static class OutboxRetryPolicy
{
    /// <param name="failedAttempts">Failures so far, at least 1.</param>
    /// <param name="random">A value in [0, 1).</param>
    public static TimeSpan Delay(int failedAttempts, TimeSpan baseDelay, TimeSpan maxDelay, double random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(random, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(random, 1d);

        // 2^30 already exceeds any sensible ceiling; the cap also keeps the arithmetic from overflowing.
        var exponent = Math.Min(failedAttempts - 1, 30);
        var ceilingTicks = Math.Min(baseDelay.Ticks * (double)(1L << exponent), maxDelay.Ticks);
        var half = ceilingTicks / 2;
        return TimeSpan.FromTicks((long)(half + (half * random)));
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>
/// Polls <see cref="OutboxProcessor"/> in the API process (DEC-302): a full batch is followed immediately by the next,
/// otherwise it waits <see cref="OutboxOptions.PollingInterval"/>; old processed messages are cleaned up every
/// <see cref="OutboxOptions.CleanupInterval"/>. Errors (e.g. the database is down) are logged and retried at the
/// next poll; they never stop the host. P2-F2 can replace this loop with a recurring job calling the same processor.
/// </summary>
internal sealed partial class OutboxWorker(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var nextCleanup = clock.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            var batchWasFull = false;
            try
            {
                batchWasFull = await processor.ProcessBatchAsync(stoppingToken) >= settings.BatchSize;
                if (clock.GetUtcNow() >= nextCleanup)
                {
                    await processor.CleanupAsync(stoppingToken);
                    nextCleanup = clock.GetUtcNow() + settings.CleanupInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The worker must outlive database outages; the next poll retries.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPollFailed(logger, exception.GetType().FullName ?? exception.GetType().Name);
            }

            if (batchWasFull)
            {
                continue;
            }

            try
            {
                await Task.Delay(settings.PollingInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox poll failed with {Error}; retrying at the next poll")]
    private static partial void LogPollFailed(ILogger logger, string error);
}

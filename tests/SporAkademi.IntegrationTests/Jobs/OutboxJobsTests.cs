using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Outbox;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>DEC-333: overlapping outbox runs (Hangfire starts one every interval) never deliver a message twice.</summary>
[Trait("Story", "P2-F2")]
public sealed class OutboxJobsTests(DatabaseFixture database) : OutboxTest(database)
{
    [Fact]
    public async Task A_run_started_while_another_is_delivering_does_nothing()
    {
        await EnqueueAsync(count: 3);
        Probe.Delay = TimeSpan.FromMilliseconds(500);

        var first = RunAsync();
        await Probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = await RunAsync();
        var delivered = await first;

        second.ShouldBe(0);
        delivered.ShouldBe(3);
        Probe.Deliveries.Select(d => d.MessageId).ShouldBeUnique();
        Probe.Deliveries.Count.ShouldBe(3);
        (await MessagesAsync()).ShouldAllBe(m => m.ProcessedAt != null);

        // The lock is released: the next run may deliver again.
        await EnqueueAsync();
        (await RunAsync()).ShouldBe(1);
    }

    private async Task<int> RunAsync()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxJobs>().ProcessAsync(Ct);
    }
}

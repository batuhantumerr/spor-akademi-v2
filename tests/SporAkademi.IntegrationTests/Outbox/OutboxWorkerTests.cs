using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>The hosted polling worker (DEC-302), switched on for this class only.</summary>
public sealed class OutboxWorkerTests(DatabaseFixture database) : OutboxTest(database)
{
    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Outbox:Enabled"] = "true";
            settings["Outbox:PollingInterval"] = "00:00:05";
            return settings;
        }
    }

    [Fact]
    public async Task The_worker_polls_on_the_interval_and_delivers_new_messages()
    {
        using var client = Host.CreateClient(); // starts the host and its hosted services
        await EnqueueAsync();

        // The worker sleeps on the fake clock; each tick lets it poll once more.
        for (var tick = 0; tick < 200 && Probe.Deliveries.IsEmpty; tick++)
        {
            Api.Clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(25), Ct);
        }

        Probe.Deliveries.ShouldHaveSingleItem();
        (await SingleMessageAsync()).ProcessedAt.ShouldNotBeNull();
    }
}

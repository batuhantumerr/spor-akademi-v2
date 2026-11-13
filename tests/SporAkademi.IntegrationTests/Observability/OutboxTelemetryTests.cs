using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Outbox;

namespace SporAkademi.IntegrationTests.Observability;

/// <summary>P4-07 (DEC-551, DEC-552): each outbox delivery is a span, and results are counted by message type.</summary>
[Trait("Story", "P4-07")]
public sealed class OutboxTelemetryTests(DatabaseFixture database) : OutboxTest(database), IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<(string Name, long Value, Dictionary<string, object?> Tags)> _measurements = new();
    private ActivityListener? _activities;
    private MeterListener? _meters;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_activities);
        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AppTelemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        _meters.Start();
    }

    [Fact]
    public async Task Deliveries_are_spans_and_results_are_counted_by_message_type()
    {
        Probe.FailNext(1);
        await EnqueueAsync();

        await ProcessAsync(); // fails, retried later
        Api.Clock.Advance(MaxRetryDelay);
        await ProcessAsync(); // delivered

        var deliveries = _spans.Where(s => s.OperationName == "outbox.deliver").ToList();
        deliveries.Count.ShouldBe(2);
        deliveries.ShouldAllBe(s => Equals(s.GetTagItem("spor_akademi.outbox.message_type"), "tests.probe.v1"));
        deliveries[0].Status.ShouldBe(ActivityStatusCode.Error);
        deliveries[0].StatusDescription.ShouldBe(typeof(InvalidOperationException).FullName);
        deliveries[1].Status.ShouldBe(ActivityStatusCode.Unset);

        var counted = _measurements.Where(m => m.Name == "spor_akademi.outbox.messages" && Equals(m.Tags["message_type"], "tests.probe.v1")).ToList();
        counted.Select(m => m.Tags["result"]).ShouldBe(["retried", "delivered"]);
        counted.ShouldAllBe(m => m.Tags.Count == 2);
    }

    public void Dispose()
    {
        _activities?.Dispose();
        _meters?.Dispose();
    }
}

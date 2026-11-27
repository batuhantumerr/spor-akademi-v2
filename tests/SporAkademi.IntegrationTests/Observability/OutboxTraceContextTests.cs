using System.Collections.Concurrent;
using System.Diagnostics;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Outbox;

namespace SporAkademi.IntegrationTests.Observability;

/// <summary>V1-04 (DEC-695 – DEC-697): an outbox row stores the writing request's trace context and the in-process delivery continues it.</summary>
[Trait("Story", "V1-04")]
public sealed class OutboxTraceContextTests(DatabaseFixture database) : OutboxTest(database), IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private ActivityListener? _activities;

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
    }

    private async Task<Activity> EnqueueInRequestAsync()
    {
        using var request = AppTelemetry.Source.StartActivity("test.request").ShouldNotBeNull();
        await EnqueueAsync();
        return request;
    }

    [Fact]
    public async Task The_row_stores_the_request_trace_context_and_a_prompt_delivery_is_its_child()
    {
        var request = await EnqueueInRequestAsync();

        var row = await SingleMessageAsync();
        row.TraceParent.ShouldBe(request.Id);

        await ProcessAsync();

        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.TraceId.ShouldBe(request.TraceId);
        delivery.ParentSpanId.ShouldBe(request.SpanId);
        delivery.Links.ShouldBeEmpty(); // the parent already is the request
    }

    [Fact]
    public async Task A_delivery_at_exactly_the_maximum_age_is_still_a_child()
    {
        var request = await EnqueueInRequestAsync();
        Api.Clock.Advance(TimeSpan.FromMinutes(5));

        await ProcessAsync();

        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.ParentSpanId.ShouldBe(request.SpanId);
    }

    [Fact]
    public async Task A_delivery_one_tick_past_the_maximum_age_is_only_linked()
    {
        var request = await EnqueueInRequestAsync();
        Api.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        await ProcessAsync();

        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.ParentSpanId.ShouldBe(default);
        delivery.Links.Single().Context.SpanId.ShouldBe(request.SpanId);
    }

    [Fact]
    public async Task A_retry_is_linked_to_the_request_and_not_its_child()
    {
        Probe.FailNext(1);
        var request = await EnqueueInRequestAsync();

        await ProcessAsync(); // first attempt: child, fails
        Api.Clock.Advance(MaxRetryDelay);
        await ProcessAsync(); // retry within the maximum age

        var deliveries = _spans.Where(s => s.OperationName == "outbox.deliver").ToList();
        deliveries.Count.ShouldBe(2);
        deliveries[0].ParentSpanId.ShouldBe(request.SpanId);
        deliveries[1].ParentSpanId.ShouldBe(default);
        deliveries[1].TraceId.ShouldNotBe(request.TraceId);
        deliveries[1].Links.Single().Context.SpanId.ShouldBe(request.SpanId);
    }

    [Fact]
    public async Task An_invalid_trace_state_does_not_lose_the_link()
    {
        var request = await EnqueueInRequestAsync();
        await using (var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE outbox.messages SET trace_state = 'not a valid state'", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        Api.Clock.Advance(TimeSpan.FromHours(1));
        await ProcessAsync();

        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.Links.Single().Context.SpanId.ShouldBe(request.SpanId);
    }

    [Fact]
    public async Task A_late_delivery_is_a_new_trace_that_only_links_to_the_request()
    {
        var request = await EnqueueInRequestAsync();
        Api.Clock.Advance(TimeSpan.FromHours(1));

        await ProcessAsync();

        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.TraceId.ShouldNotBe(request.TraceId);
        delivery.ParentSpanId.ShouldBe(default);
        delivery.Links.Single().Context.TraceId.ShouldBe(request.TraceId);
        delivery.Links.Single().Context.SpanId.ShouldBe(request.SpanId);
    }

    [Fact]
    public async Task A_row_written_before_the_trace_columns_existed_is_still_delivered()
    {
        await EnqueueAsync();
        await using (var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE outbox.messages SET trace_parent = NULL, trace_state = NULL", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await ProcessAsync();

        (await SingleMessageAsync()).ProcessedAt.ShouldNotBeNull();
        Probe.Deliveries.ShouldHaveSingleItem();
        var delivery = _spans.Single(s => s.OperationName == "outbox.deliver");
        delivery.Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_row_outside_any_span_stores_no_context()
    {
        await EnqueueAsync();

        var row = await SingleMessageAsync();
        row.TraceParent.ShouldBeNull();
        row.TraceState.ShouldBeNull();
    }

    public void Dispose() => _activities?.Dispose();
}

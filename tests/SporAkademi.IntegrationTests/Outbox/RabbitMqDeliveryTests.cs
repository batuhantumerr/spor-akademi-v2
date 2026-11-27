using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using SporAkademi.Domain.Organization;
using SporAkademi.Infrastructure.Messaging;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>P5-03: the outbox publishes through RabbitMQ and the consumers run the handlers (DEC-620 – DEC-629).</summary>
[Trait("Story", "P5-03")]
public sealed class RabbitMqDeliveryTests(DatabaseFixture database) : RabbitMqOutboxTest(database)
{
    [Fact]
    public async Task A_published_message_is_consumed_end_to_end_and_the_trace_spans_both_sides()
    {
        await EnqueueAsync();

        await ProcessAsync();

        // Processed only after the broker's confirm; the handler has not necessarily run yet (it is another process's job).
        (await SingleMessageAsync()).ProcessedAt.ShouldNotBeNull();
        await EventuallyAsync(() => !Probe.Deliveries.IsEmpty, "the consumer to handle the message");
        var delivery = Probe.Deliveries.ShouldHaveSingleItem();
        delivery.MessageId.ShouldBe((await SingleMessageAsync()).Id);
        await EventuallyAsync(async () => await InboxRowsAsync() == 1, "the inbox row");
        Count("published").ShouldBe(1);
        await EventuallyAsync(() => Count("consumed") == 1, "the consumed metric");

        // W3C trace context travelled in the message headers: the consumer span is a child of the publish span.
        await EventuallyAsync(() => { lock (Activities) { return Activities.Any(a => a.OperationName == "messaging.consume"); } }, "the consumer span");
        System.Diagnostics.Activity publish;
        System.Diagnostics.Activity consume;
        lock (Activities)
        {
            publish = Activities.Single(a => a.OperationName == "outbox.publish");
            consume = Activities.Single(a => a.OperationName == "messaging.consume");
        }

        consume.TraceId.ShouldBe(publish.TraceId);
        consume.ParentSpanId.ShouldBe(publish.SpanId);
    }

    [Fact]
    [Trait("Story", "V1-04")]
    public async Task The_consumer_span_is_in_the_trace_of_the_request_that_wrote_the_message()
    {
        System.Diagnostics.Activity request;
        using (request = SporAkademi.Application.Abstractions.Observability.AppTelemetry.Source.StartActivity("test.request").ShouldNotBeNull())
        {
            await EnqueueAsync();
        }

        await ProcessAsync();

        await EventuallyAsync(() => { lock (Activities) { return Activities.Any(a => a.OperationName == "messaging.consume"); } }, "the consumer span");
        System.Diagnostics.Activity publish;
        System.Diagnostics.Activity consume;
        lock (Activities)
        {
            publish = Activities.Single(a => a.OperationName == "outbox.publish");
            consume = Activities.Single(a => a.OperationName == "messaging.consume");
        }

        System.Diagnostics.Activity deliver;
        lock (Activities)
        {
            deliver = Activities.Single(a => a.OperationName == "outbox.deliver");
        }

        deliver.ParentSpanId.ShouldBe(request.SpanId);
        publish.TraceId.ShouldBe(request.TraceId);
        publish.ParentSpanId.ShouldBe(deliver.SpanId);
        consume.TraceId.ShouldBe(request.TraceId);
        consume.ParentSpanId.ShouldBe(publish.SpanId);
    }

    [Fact]
    [Trait("Story", "V1-04")]
    public async Task A_message_without_a_stored_context_is_published_and_consumed()
    {
        await EnqueueAsync();
        await using (var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE outbox.messages SET trace_parent = NULL, trace_state = NULL", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await ProcessAsync();

        await EventuallyAsync(() => !Probe.Deliveries.IsEmpty, "the consumer to handle the message");
    }

    [Fact]
    public async Task A_message_delivered_twice_is_handled_once()
    {
        await EnqueueAsync();
        await ProcessAsync();
        await EventuallyAsync(() => !Probe.Deliveries.IsEmpty, "the first delivery");

        // The crash window between the broker's confirm and the outbox commit: the same message is published again.
        await using (var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new Npgsql.NpgsqlCommand("UPDATE outbox.messages SET processed_at = NULL", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await ProcessAsync();

        await EventuallyAsync(() => Count("duplicate") == 1, "the duplicate to be recognised");
        Probe.Deliveries.Count.ShouldBe(1);
        (await InboxRowsAsync()).ShouldBe(1);
        Count("published").ShouldBe(2);
        (await QueueDepthAsync(ProbeQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task A_poison_message_is_retried_then_dead_lettered_after_the_maximum_attempts()
    {
        Probe.AlwaysFail = true;
        await EnqueueAsync();

        await ProcessAsync();

        await EventuallyAsync(async () => await QueueDepthAsync(DeadLetterQueue) == 1, "the message in the dead-letter queue");
        Probe.Deliveries.Count.ShouldBe(3); // MaxDeliveryAttempts
        var dead = (await TakeAsync(DeadLetterQueue)).ShouldNotBeNull();
        dead.BasicProperties.MessageId.ShouldBe((await SingleMessageAsync()).Id.ToString());
        RabbitMqBroker.HeaderString(dead.BasicProperties.Headers, RabbitMqBroker.OriginalQueueHeader).ShouldBe(ProbeQueue);
        RabbitMqBroker.HeaderString(dead.BasicProperties.Headers, RabbitMqBroker.AttemptHeader).ShouldBe("3");
        // The error is an exception type, never a message.
        RabbitMqBroker.HeaderString(dead.BasicProperties.Headers, RabbitMqBroker.ErrorHeader).ShouldBe(typeof(InvalidOperationException).FullName);

        (await InboxRowsAsync()).ShouldBe(0); // a failed attempt rolls its inbox row back
        (await QueueDepthAsync(ProbeQueue)).ShouldBe(0u);
        await EventuallyAsync(() => Count("dead_lettered") == 1, "the dead-lettered metric");
        Count("failed").ShouldBe(3);
    }

    [Fact]
    public async Task A_message_that_fails_once_is_retried_and_then_succeeds()
    {
        Probe.FailNext(1);
        await EnqueueAsync();

        await ProcessAsync();

        await EventuallyAsync(() => Probe.Deliveries.Count == 2, "the retry");
        Probe.Deliveries.Select(d => d.Attempt).ShouldBe([1, 2], ignoreOrder: false);
        await EventuallyAsync(async () => await InboxRowsAsync() == 1, "the inbox row");
        (await QueueDepthAsync(DeadLetterQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task A_broker_outage_leaves_the_message_in_the_outbox_and_it_is_delivered_after_recovery()
    {
        // Connect once so the outage hits an established connection.
        await EnqueueAsync();
        await ProcessAsync();
        await EventuallyAsync(() => !Probe.Deliveries.IsEmpty, "the first delivery");

        await StopBrokerAsync();
        await EnqueueAsync();
        var pending = (await MessagesAsync()).Single(m => m.ProcessedAt is null);

        await ProcessAsync();

        var kept = (await MessagesAsync()).Single(m => m.Id == pending.Id);
        kept.ProcessedAt.ShouldBeNull();
        kept.DeadLetteredAt.ShouldBeNull();
        kept.Attempts.ShouldBe(0); // an outage does not use up the message's attempts
        kept.LastError.ShouldBe(OutboxProcessor.BrokerUnavailableError);
        kept.NextAttemptAt.ShouldBeGreaterThan(Api.Clock.GetUtcNow());
        Count("broker_unavailable").ShouldBeGreaterThan(0);

        await StartBrokerAsync();

        await EventuallyAsync(async () =>
        {
            Api.Clock.Advance(TimeSpan.FromMinutes(5));
            await ProcessAsync();
            return (await MessagesAsync()).Single(m => m.Id == pending.Id).ProcessedAt is not null;
        }, "the postponed message to be published");
        await EventuallyAsync(() => Probe.Deliveries.Any(d => d.MessageId == pending.Id), "the consumer to handle it after recovery");
        Probe.Deliveries.Count(d => d.MessageId == pending.Id).ShouldBe(1);
    }

    [Fact]
    public async Task The_consumer_runs_in_the_school_of_the_message()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        foreach (var (school, name) in new[] { (schoolA, "Yüzme"), (schoolB, "Basketbol") })
        {
            await using var scope = Host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(school.Value);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(Sport.Create(school, name).Value);
            await db.SaveChangesAsync(Ct);
        }

        Probe.InspectTenant = true;
        await EnqueueAsync(tenant: schoolA.Value);
        await EnqueueAsync(tenant: schoolB.Value);
        await EnqueueAsync(tenant: null);

        await ProcessAsync();

        await EventuallyAsync(() => Probe.Deliveries.Count == 3, "all three deliveries");
        var messages = await MessagesAsync();
        var deliveries = Probe.Deliveries.ToDictionary(d => d.MessageId);
        foreach (var message in messages)
        {
            var delivery = deliveries[message.Id];
            delivery.TenantId.ShouldBe(message.TenantId);
            // Tenant filter and row level security: a school's consumer sees its own school only, a platform message none.
            delivery.VisibleSportSchools.ShouldBe(message.TenantId is { } tenant ? [tenant] : []);
        }

        (await InboxRowsAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task A_type_without_a_consumer_is_not_marked_processed()
    {
        var result = await InUnitOfWorkAsync((outbox, _, _) =>
        {
            outbox.Enqueue(new OrphanMessage(Guid.CreateVersion7()), null);
            return Task.FromResult(SporAkademi.Domain.SharedKernel.Result.Success(1));
        });
        result.IsSuccess.ShouldBeTrue();

        await ProcessAsync();

        var message = await SingleMessageAsync();
        message.ProcessedAt.ShouldBeNull();
        message.LastError.ShouldBe(OutboxProcessor.NoHandlerError);
        message.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task A_message_with_an_unreadable_payload_is_dead_lettered_without_retries()
    {
        await WaitForTopologyAsync();

        await PublishRawAsync("{\"unexpected\": true}", new() { [RabbitMqBroker.AttemptHeader] = 1 });

        await EventuallyAsync(async () => await QueueDepthAsync(DeadLetterQueue) == 1, "the poison message in the dead-letter queue");
        var dead = (await TakeAsync(DeadLetterQueue)).ShouldNotBeNull();
        RabbitMqBroker.HeaderString(dead.BasicProperties.Headers, RabbitMqBroker.ErrorHeader).ShouldBe(OutboxProcessor.InvalidPayloadError);
        Probe.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_message_with_an_unparsable_tenant_header_is_dead_lettered_not_run_in_platform_scope()
    {
        await WaitForTopologyAsync();
        var payload = System.Text.Json.JsonSerializer.Serialize(new ProbeMessage(Guid.CreateVersion7(), "x"));

        await PublishRawAsync(payload, new() { [RabbitMqBroker.AttemptHeader] = 1, [RabbitMqBroker.TenantHeader] = "not-a-guid" });

        await EventuallyAsync(async () => await QueueDepthAsync(DeadLetterQueue) == 1, "the message in the dead-letter queue");
        var dead = (await TakeAsync(DeadLetterQueue)).ShouldNotBeNull();
        RabbitMqBroker.HeaderString(dead.BasicProperties.Headers, RabbitMqBroker.ErrorHeader).ShouldBe(RabbitMqConsumerService.InvalidTenantError);
        Probe.Deliveries.ShouldBeEmpty();
        (await InboxRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_delivery_that_cannot_be_settled_is_dead_lettered_by_the_broker_instead_of_looping()
    {
        await WaitForTopologyAsync();
        // The publisher declares the topology when it first publishes: let that happen before the queue is deleted.
        await EnqueueAsync();
        await ProcessAsync();
        await EventuallyAsync(() => Probe.Deliveries.Count == 1, "the warm-up delivery");
        // The handler fails and the retry copy has nowhere to go: the delivery is requeued again and again.
        await DeleteQueueAsync(ProbeQueue + ".retry");
        Probe.AlwaysFail = true;
        await EnqueueAsync();

        await ProcessAsync();

        await EventuallyAsync(async () => await QueueDepthAsync(DeadLetterQueue) == 1, "the delivery limit to dead-letter it");
        await EventuallyAsync(() => Count("settle_failed") > 0, "the settle_failed metric");
        (await QueueDepthAsync(ProbeQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Readiness_is_degraded_while_the_broker_is_unreachable()
    {
        await WaitForTopologyAsync();
        var check = Host.Services.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        var up = await check.CheckHealthAsync(r => r.Name == "rabbitmq", Ct);
        up.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);

        await StopBrokerAsync();

        var down = await check.CheckHealthAsync(r => r.Name == "rabbitmq", Ct);
        down.Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded);
    }
}

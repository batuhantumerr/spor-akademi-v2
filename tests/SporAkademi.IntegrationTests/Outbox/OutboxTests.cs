using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>P2-F3 (DEC-300 – DEC-303): the transactional outbox on real PostgreSQL, as the app role.</summary>
public sealed class OutboxTests(DatabaseFixture database) : OutboxTest(database)
{
    private static School NewSchool(string slug = "outbox-okulu") =>
        School.Create("Outbox Okulu", Slug.Create(slug).Value, "Europe/Istanbul", RetentionPolicy.Default).Value;

    [Fact]
    public async Task A_message_is_stored_with_the_business_change_in_the_same_transaction()
    {
        var school = NewSchool();

        var result = await InUnitOfWorkAsync((outbox, db, _) =>
        {
            db.Add(school);
            outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "created"), school.Id.Value);
            return Task.FromResult(Result.Success(1));
        });

        result.IsSuccess.ShouldBeTrue();
        (await CountSchoolsAsync()).ShouldBe(1);
        var message = await SingleMessageAsync();
        message.Type.ShouldBe("tests.probe.v1");
        message.TenantId.ShouldBe(school.Id.Value);
        message.OccurredAt.ShouldBe(Api.Clock.GetUtcNow());
        message.NextAttemptAt.ShouldBe(Api.Clock.GetUtcNow());
        message.Attempts.ShouldBe(0);
        message.ProcessedAt.ShouldBeNull();
        message.Payload.ShouldContain("\"label\": \"created\"");
    }

    [Fact]
    public async Task A_failed_result_stores_neither_the_change_nor_the_message()
    {
        var result = await InUnitOfWorkAsync((outbox, db, _) =>
        {
            db.Add(NewSchool());
            outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "rolled back"), null);
            return Task.FromResult(Result.Failure<int>(SchoolErrors.SlugTaken));
        });

        result.IsFailure.ShouldBeTrue();
        (await CountSchoolsAsync()).ShouldBe(0);
        (await MessagesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_exception_after_saving_rolls_back_the_change_and_the_message()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => InUnitOfWorkAsync(async (outbox, db, ct) =>
        {
            db.Add(NewSchool());
            outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "saved then failed"), null);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("boom");
        }));

        (await CountSchoolsAsync()).ShouldBe(0);
        (await MessagesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Enqueueing_outside_a_transaction_is_refused()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<Application.Abstractions.Outbox.IOutbox>();

        Should.Throw<InvalidOperationException>(() => outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "no transaction"), null));
    }

    [Fact]
    public async Task A_school_cannot_enqueue_a_message_for_another_school()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));

        await Should.ThrowAsync<TenantIsolationException>(() => InUnitOfWorkAsync(
            (outbox, _, _) =>
            {
                outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "leak"), schoolB.Value);
                return Task.FromResult(Result.Success(1));
            },
            actingTenant: schoolA.Value));

        (await MessagesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_processor_delivers_after_commit_and_marks_the_message_processed()
    {
        await EnqueueAsync();

        (await ProcessAsync()).ShouldBe(1);

        var message = await SingleMessageAsync();
        message.ProcessedAt.ShouldBe(Api.Clock.GetUtcNow());
        message.LastError.ShouldBeNull();
        var delivery = Probe.Deliveries.ShouldHaveSingleItem();
        delivery.MessageId.ShouldBe(message.Id);
        delivery.Attempt.ShouldBe(1);
        (await ProcessAsync()).ShouldBe(0);
        Probe.Deliveries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_handler_is_retried_with_backoff_under_the_same_message_id()
    {
        Probe.FailNext(1);
        await EnqueueAsync();
        var failedAt = Api.Clock.GetUtcNow();

        await ProcessAsync();

        var failed = await SingleMessageAsync();
        failed.ProcessedAt.ShouldBeNull();
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldBe(typeof(InvalidOperationException).FullName);
        failed.NextAttemptAt.ShouldBeInRange(failedAt + (BaseRetryDelay / 2), failedAt + BaseRetryDelay);

        // Not due yet: nothing is claimed.
        Api.Clock.Advance(BaseRetryDelay / 2 - TimeSpan.FromSeconds(1));
        (await ProcessAsync()).ShouldBe(0);

        Api.Clock.Advance(BaseRetryDelay);
        (await ProcessAsync()).ShouldBe(1);

        var delivered = await SingleMessageAsync();
        delivered.ProcessedAt.ShouldNotBeNull();
        delivered.Attempts.ShouldBe(1);
        delivered.LastError.ShouldBeNull();
        Probe.Deliveries.Select(d => d.Attempt).ShouldBe([1, 2]);
        Probe.Deliveries.Select(d => d.MessageId).Distinct().ShouldHaveSingleItem().ShouldBe(delivered.Id);
    }

    [Fact]
    public async Task A_message_is_dead_lettered_after_the_maximum_attempts_and_the_health_check_degrades()
    {
        Probe.AlwaysFail = true;
        await EnqueueAsync();
        (await OutboxHealthAsync()).ShouldBe(HealthStatus.Healthy);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            (await ProcessAsync()).ShouldBe(1);
            Api.Clock.Advance(MaxRetryDelay);
        }

        var message = await SingleMessageAsync();
        message.Attempts.ShouldBe(MaxAttempts);
        message.DeadLetteredAt.ShouldNotBeNull();
        message.ProcessedAt.ShouldBeNull();
        (await ProcessAsync()).ShouldBe(0);
        Probe.Deliveries.Count.ShouldBe(MaxAttempts);
        (await OutboxHealthAsync()).ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task An_unknown_message_type_is_retried_and_then_dead_lettered_without_running_anything()
    {
        await using (var scope = Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Add(new OutboxMessage(Guid.CreateVersion7(), "System.Diagnostics.Process, System", "{}", null, Api.Clock.GetUtcNow()));
            await db.SaveChangesAsync(Ct);
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await ProcessAsync();
            Api.Clock.Advance(MaxRetryDelay);
        }

        var message = await SingleMessageAsync();
        message.LastError.ShouldBe(OutboxProcessor.UnknownTypeError);
        message.DeadLetteredAt.ShouldNotBeNull();
        Probe.Deliveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Parallel_processors_never_deliver_the_same_message_twice()
    {
        const int Messages = 40;
        Probe.Delay = TimeSpan.FromMilliseconds(20);
        await EnqueueAsync(Messages);

        // Independent processor instances, like several API instances polling the same table.
        var processors = Enumerable.Range(0, 4)
            .Select(_ => ActivatorUtilities.CreateInstance<OutboxProcessor>(Host.Services))
            .ToList();
        await Task.WhenAll(processors.Select(processor => Task.Run(
            async () =>
            {
                while (await processor.ProcessBatchAsync(Ct) > 0)
                {
                }
            },
            Ct)));
        await ProcessAsync();

        Probe.Deliveries.Count.ShouldBe(Messages);
        Probe.Deliveries.Select(d => d.MessageId).Distinct().Count().ShouldBe(Messages);
        Probe.MaxConcurrency.ShouldBeGreaterThan(1);
        (await MessagesAsync()).ShouldAllBe(m => m.ProcessedAt != null);
    }

    [Fact]
    public async Task Handlers_run_as_the_message_school_with_its_tenant_filter()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        await using (var scope = Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(Sport.Create(schoolA, "Yüzme").Value, Sport.Create(schoolB, "Basketbol").Value);
            await db.SaveChangesAsync(Ct);
        }

        Probe.InspectTenant = true;
        await EnqueueAsync(tenant: schoolA.Value);
        await EnqueueAsync(tenant: schoolB.Value);
        await EnqueueAsync(tenant: null);

        await ProcessAsync();

        var messages = await MessagesAsync();
        var deliveries = Probe.Deliveries.ToDictionary(d => d.MessageId);
        deliveries.Count.ShouldBe(3);
        foreach (var message in messages)
        {
            var delivery = deliveries[message.Id];
            delivery.TenantId.ShouldBe(message.TenantId);
            delivery.VisibleSportSchools.ShouldBe(message.TenantId is { } tenant ? [tenant] : []);
        }

        messages.Select(m => m.TenantId).ShouldBe([schoolA.Value, schoolB.Value, null], ignoreOrder: true);
    }

    [Fact]
    public async Task Cleanup_deletes_only_processed_messages_past_retention()
    {
        await EnqueueAsync();
        await ProcessAsync(); // processed now: past retention after the clock moves on

        Api.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await EnqueueAsync();
        await ProcessAsync(); // processed recently: kept
        Probe.AlwaysFail = true;
        await EnqueueAsync(); // pending (failing): kept
        await ProcessAsync();
        var before = await MessagesAsync();

        Api.Clock.Advance(TimeSpan.FromMinutes(1));
        var deleted = await Processor.CleanupAsync(Ct);

        deleted.ShouldBe(1);
        var expired = before.Where(m => m.ProcessedAt < Api.Clock.GetUtcNow() - TimeSpan.FromDays(1)).ShouldHaveSingleItem();
        (await MessagesAsync()).Select(m => m.Id).ShouldBe(before.Where(m => m != expired).Select(m => m.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task The_health_check_degrades_when_a_pending_message_is_overdue()
    {
        await EnqueueAsync();
        (await OutboxHealthAsync()).ShouldBe(HealthStatus.Healthy);

        Api.Clock.Advance(TimeSpan.FromMinutes(16));

        (await OutboxHealthAsync()).ShouldBe(HealthStatus.Degraded);
    }

    private async Task<HealthStatus> OutboxHealthAsync()
    {
        var report = await Host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == "outbox", Ct);
        return report.Entries["outbox"].Status;
    }

    private async Task<int> CountSchoolsAsync()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<School>().CountAsync(Ct);
    }
}

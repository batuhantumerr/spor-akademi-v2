using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Outbox;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>
/// The Hangfire server in the API process, connected as the app role (DEC-330, DEC-331, DEC-333): it runs the
/// recurring outbox job, which delivers messages through the unchanged <see cref="OutboxProcessor"/>.
/// </summary>
[Trait("Story", "P2-F2")]
public sealed class JobServerTests(DatabaseFixture database) : OutboxTest(database)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["Outbox:Scheduler"] = "Hangfire";
            settings["Jobs:ServerEnabled"] = "true";
            settings["Jobs:OutboxCron"] = "* * * * * *"; // every second
            settings["Jobs:SchedulePollingInterval"] = "00:00:01";
            settings["Jobs:QueuePollInterval"] = "00:00:01";
            return settings;
        }
    }

    [Fact]
    public async Task The_recurring_outbox_job_delivers_messages_as_the_app_role()
    {
        using var client = Host.CreateClient(); // starts the host, its job server and the registration
        await EnqueueAsync(count: 2);

        await WaitUntilAsync(() => Probe.Deliveries.Count >= 2);

        (await MessagesAsync()).ShouldAllBe(m => m.ProcessedAt != null);
        Host.Services.GetRequiredService<JobStorage>().GetMonitoringApi().SucceededListCount().ShouldBeGreaterThan(0);
        new NpgsqlConnectionStringBuilder(Database.AppConnectionString).Username.ShouldBe("spor_akademi_app");
    }

    [Fact]
    public async Task The_server_registers_the_recurring_jobs_and_reports_ready()
    {
        using var client = Host.CreateClient();
        var storage = Host.Services.GetRequiredService<JobStorage>();

        await WaitUntilAsync(() => RecurringJobIds(storage).Count == 5);

        RecurringJobIds(storage).ShouldBe([OutboxJobs.CleanupJobId, OutboxJobs.ProcessJobId, RefreshTokenCleanupJob.JobId, DuesReminderJob.JobId, StudentAnonymizationJob.JobId], ignoreOrder: true);
        var health = Host.Services.GetRequiredService<HealthCheckService>();
        await WaitUntilAsync(async () => (await health.CheckHealthAsync(c => c.Name == "jobs", Ct)).Status == HealthStatus.Healthy);
    }

    [Fact]
    public async Task Without_a_heartbeat_readiness_is_degraded_not_unhealthy()
    {
        // The server is enabled but this process's heartbeat never shows up under its name.
        var clock = new HeartbeatClock(TimeProvider.System);
        var check = new JobsHealthCheck(
            new JobsMonitoringSnapshot(Host.Services.GetRequiredService<JobStorage>(), new JobServerIdentity(), clock),
            clock,
            Microsoft.Extensions.Options.Options.Create(new JobsOptions { ServerEnabled = true }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task A_process_with_another_outbox_scheduler_removes_the_outbox_jobs()
    {
        // Registered earlier, e.g. by a process that used Hangfire for the outbox (Api runs no server).
        var manager = Api.Services.GetRequiredService<IRecurringJobManager>();
        manager.AddOrUpdate<OutboxJobs>(OutboxJobs.ProcessJobId, job => job.ProcessAsync(CancellationToken.None), Cron.Minutely());
        await using var other = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:ServerEnabled"] = "true", ["Outbox:Scheduler"] = "Worker" })));
        var storage = other.Services.GetRequiredService<JobStorage>();
        using var client = other.CreateClient();

        await WaitUntilAsync(() => RecurringJobIds(storage).SetEquals([RefreshTokenCleanupJob.JobId, DuesReminderJob.JobId, StudentAnonymizationJob.JobId]));
    }

    [Fact]
    public async Task A_failed_job_keeps_only_the_exception_type_in_the_storage()
    {
        using var client = Host.CreateClient();
        var storage = Host.Services.GetRequiredService<JobStorage>();
        var id = Host.Services.GetRequiredService<IBackgroundJobClient>().Enqueue(() => FailingJob.Run());

        await WaitUntilAsync(() => storage.GetMonitoringApi().JobDetails(id)?.History.FirstOrDefault()?.StateName == "Failed");

        var failed = storage.GetMonitoringApi().JobDetails(id).History.First().Data;
        failed["ExceptionType"].ShouldBe(typeof(JobFailedException).FullName);
        failed["ExceptionMessage"].ShouldBe(typeof(InvalidOperationException).FullName);
        string.Join(' ', failed.Values).ShouldNotContain(FailingJob.PersonalData);
    }

    [Fact]
    public async Task The_api_stays_up_when_the_job_server_cannot_reach_the_database()
    {
        await using var factory = new ApiFactory("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");
        await using var unreachable = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:ServerEnabled"] = "true", ["Outbox:Scheduler"] = "Hangfire" })));
        using var http = unreachable.CreateClient();

        await Task.Delay(TimeSpan.FromSeconds(5), Ct); // several failed attempts to reach the storage

        unreachable.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
        (await http.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    private static HashSet<string> RecurringJobIds(JobStorage storage)
    {
        using var connection = storage.GetConnection();
        return [.. connection.GetRecurringJobs().Select(j => j.Id)];
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (!await condition())
        {
            TimeProvider.System.GetUtcNow().ShouldBeLessThan(deadline, "The job server did not get there in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }
}

/// <summary>A job whose exception message carries (synthetic) personal data.</summary>
public static class FailingJob
{
    public const string PersonalData = "veli.kaya@example.test";

    [AutomaticRetry(Attempts = 0)]
    public static void Run() => throw new InvalidOperationException($"Duplicate e-mail {PersonalData}");
}

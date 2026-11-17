using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using SporAkademi.Application.Abstractions.Notifications;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure;
using SporAkademi.Infrastructure.Messaging;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.Notifications;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>An e-mail message of the tests: its handler is in the notifications group, like the real e-mail handlers.</summary>
[OutboxMessage("tests.probe-email.v1")]
public sealed record ProbeEmailMessage(Guid Value) : IOutboxMessage;

[OutboxConsumerGroup(OutboxConsumerGroups.Notifications)]
internal sealed class ProbeEmailHandler(IEmailSender sender) : IOutboxMessageHandler<ProbeEmailMessage>
{
    public Task Handle(ProbeEmailMessage message, OutboxMessageContext context, CancellationToken cancellationToken) =>
        sender.SendAsync(new EmailMessage("probe", "synthetic@example.test", "probe", message.Value.ToString(), message.Value.ToString()), cancellationToken);
}

/// <summary>
/// P5-04 (DEC-630 – DEC-633): the API (notification consumers excluded) and the notifications worker against one database and
/// one real RabbitMQ. Each host has its own recording e-mail sender, so which one sent is visible.
/// </summary>
[Trait("Story", "P5-04")]
public sealed class NotificationsWorkerTests(DatabaseFixture database) : RabbitMqOutboxTest(database)
{
    private const string EmailQueue = "tests.probe-email.v1.ProbeEmailHandler";

    private readonly CapturingEmailSender _workerEmails = new();
    private readonly ProbeLog _workerProbe = new();
    private WebApplication? _worker;

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            // The API's configuration while the worker is deployed (infra/ sets the same through an environment variable).
            settings["RabbitMq:Consumers:ExcludeGroups:0"] = OutboxConsumerGroups.Notifications;
            return settings;
        }
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddOutboxMessageType(typeof(ProbeEmailMessage));
        services.AddScoped<IOutboxMessageHandler<ProbeEmailMessage>, ProbeEmailHandler>();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync(CancellationToken.None);
            await _worker.DisposeAsync();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task StartWorkerAsync()
    {
        var settings = Settings;
        _worker = NotificationsHost.Create(
            [],
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = Database.AppConnectionString,
                ["ConnectionStrings:RabbitMq"] = settings["ConnectionStrings:RabbitMq"],
                ["RabbitMq:RetryDelay"] = settings["RabbitMq:RetryDelay"],
                ["RabbitMq:MaxDeliveryAttempts"] = settings["RabbitMq:MaxDeliveryAttempts"],
                ["RabbitMq:ConnectTimeout"] = settings["RabbitMq:ConnectTimeout"],
                ["Serilog:MinimumLevel:Default"] = "Fatal",
                ["Urls"] = "http://127.0.0.1:0",
                // A deployment that tries to widen the worker: the fixed settings win.
                ["RabbitMq:Consumers:Modules:0"] = "billing",
                ["RabbitMq:Consumers:ExcludeGroups:0"] = OutboxConsumerGroups.Notifications,
                ["Jobs:ServerEnabled"] = "true",
            },
            environment: "Testing",
            configureServices: services =>
            {
                services.AddOutboxMessageType(typeof(ProbeMessage));
                services.AddOutboxMessageType(typeof(ProbeEmailMessage));
                services.AddSingleton(_workerProbe);
                services.AddScoped<IOutboxMessageHandler<ProbeMessage>, ProbeHandler>();
                services.AddScoped<IOutboxMessageHandler<ProbeEmailMessage>, ProbeEmailHandler>();
                services.Replace(ServiceDescriptor.Singleton<IEmailSender>(_workerEmails));
            });
        await _worker.StartAsync(Ct);
        var state = _worker.Services.GetRequiredService<ConsumerState>();
        await EventuallyAsync(() => state.Expected > 0 && state.Running == state.Expected, "the worker's consumers to be running");
    }

    private async Task<uint> ConsumersOfAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(Settings["ConnectionStrings:RabbitMq"]!) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        return (await channel.QueueDeclarePassiveAsync(queue, Ct)).ConsumerCount;
    }

    private async Task EnqueueEmailAndProbeAsync()
    {
        var result = await InUnitOfWorkAsync((outbox, _, _) =>
        {
            outbox.Enqueue(new ProbeEmailMessage(Guid.CreateVersion7()), tenantId: null);
            outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), "probe"), tenantId: null);
            return Task.FromResult(Result.Success(2));
        });
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_notification_is_sent_once_by_the_worker_while_other_messages_stay_with_the_api()
    {
        await WaitForTopologyAsync(); // the API's consumers
        await StartWorkerAsync();

        await EnqueueEmailAndProbeAsync();
        await ProcessAsync();

        await EventuallyAsync(() => _workerEmails.Sent.Count == 1, "the worker to send the e-mail");
        await EventuallyAsync(() => Probe.Deliveries.Count == 1, "the API to handle the other message");
        await EventuallyAsync(async () => await InboxRowsAsync() == 2, "both inbox rows");

        // One consumer on the e-mail queue (the worker's), one on the other (the API's); nobody else handled either message.
        (await ConsumersOfAsync(EmailQueue)).ShouldBe(1u);
        (await ConsumersOfAsync(ProbeQueue)).ShouldBe(1u);
        Api.Emails.Sent.ShouldBeEmpty();
        _workerEmails.Sent.Count.ShouldBe(1);
        _workerProbe.Deliveries.ShouldBeEmpty();
        (await QueueDepthAsync(EmailQueue)).ShouldBe(0u);
    }

    [Fact]
    public async Task Without_the_worker_the_api_leaves_the_notification_waiting_in_its_queue_and_the_worker_sends_it_later()
    {
        await WaitForTopologyAsync();
        await EnqueueEmailAndProbeAsync();
        await ProcessAsync();

        // The other message is a barrier: the API consumed it, so it had the chance to consume the e-mail message too.
        await EventuallyAsync(() => Probe.Deliveries.Count == 1, "the API to handle the other message");
        (await ConsumersOfAsync(EmailQueue)).ShouldBe(0u);
        (await QueueDepthAsync(EmailQueue)).ShouldBe(1u);
        Api.Emails.Sent.ShouldBeEmpty();

        await StartWorkerAsync();

        await EventuallyAsync(() => _workerEmails.Sent.Count == 1, "the worker to send the waiting e-mail");
        await EventuallyAsync(async () => await QueueDepthAsync(EmailQueue) == 0, "the queue to drain");
        Api.Emails.Sent.ShouldBeEmpty();
        _workerEmails.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_worker_answers_its_health_probes_and_is_ready_once_its_consumers_run()
    {
        await StartWorkerAsync();
        using var client = new HttpClient { BaseAddress = new Uri(_worker!.Urls.First()) };

        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // Nothing else is served: the worker has no API surface.
        (await client.GetAsync(new Uri("/api/v1/students", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_deployments_settings_cannot_widen_the_workers_consumers()
    {
        await WaitForTopologyAsync(); // the API's consumers
        await StartWorkerAsync();

        // Settings in StartWorkerAsync tried Modules=billing, ExcludeGroups=notifications and the Hangfire server.
        var options = _worker!.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RabbitMqOptions>>().Value;
        options.Consumers.Groups.ShouldBe([OutboxConsumerGroups.Notifications]);
        options.Consumers.Modules.ShouldBeEmpty();
        options.Consumers.ExcludeGroups.ShouldBeEmpty();
        options.Consumers.Enabled.ShouldBeTrue();
        var jobs = _worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SporAkademi.Infrastructure.Jobs.JobsOptions>>().Value;
        jobs.ServerEnabled.ShouldBeFalse();
        // The probe message's handler is not in the group: no consumer of the worker's, only the API's.
        (await ConsumersOfAsync(ProbeQueue)).ShouldBe(1u);
    }
}

/// <summary>P5-04: which consumers a host selects (DEC-630) and when the worker refuses to start.</summary>
[Trait("Story", "P5-04")]
public sealed class ConsumerSelectionTests
{
    [Fact]
    public void By_default_every_consumer_runs()
    {
        var selection = new ConsumerSelection();

        selection.Includes("billing", null).ShouldBeTrue();
        selection.Includes("identity", OutboxConsumerGroups.Notifications).ShouldBeTrue();
    }

    [Fact]
    public void An_excluded_group_does_not_run_but_ungrouped_handlers_of_the_same_module_do()
    {
        var selection = new ConsumerSelection { ExcludeGroups = [OutboxConsumerGroups.Notifications] };

        selection.Includes("billing", OutboxConsumerGroups.Notifications).ShouldBeFalse();
        selection.Includes("billing", null).ShouldBeTrue();
    }

    [Fact]
    public void A_group_selection_runs_that_group_only_and_exclusion_wins()
    {
        var only = new ConsumerSelection { Groups = [OutboxConsumerGroups.Notifications] };
        only.Includes("billing", OutboxConsumerGroups.Notifications).ShouldBeTrue();
        only.Includes("billing", null).ShouldBeFalse();
        only.Includes("billing", "other").ShouldBeFalse();

        var both = new ConsumerSelection { Groups = [OutboxConsumerGroups.Notifications], ExcludeGroups = [OutboxConsumerGroups.Notifications] };
        both.Includes("billing", OutboxConsumerGroups.Notifications).ShouldBeFalse();
    }

    [Fact]
    public void The_module_filter_still_applies_next_to_the_group_filter()
    {
        var selection = new ConsumerSelection { Modules = ["identity"], Groups = [OutboxConsumerGroups.Notifications] };

        selection.Includes("identity", OutboxConsumerGroups.Notifications).ShouldBeTrue();
        selection.Includes("billing", OutboxConsumerGroups.Notifications).ShouldBeFalse();
    }

    [Fact]
    public void The_worker_refuses_to_start_without_a_broker()
    {
        var exception = Should.Throw<InvalidOperationException>(() => NotificationsHost.Create(
            [],
            new Dictionary<string, string?> { ["ConnectionStrings:App"] = "Host=localhost;Database=x" },
            environment: "Testing"));

        exception.Message.ShouldContain("ConnectionStrings:RabbitMq");
    }

    [Fact]
    public void The_worker_refuses_to_start_in_production_without_an_smtp_host_because_it_would_lose_e_mails()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = "Host=localhost;Database=x",
            ["ConnectionStrings:RabbitMq"] = "amqp://u:p@localhost:5672/",
        };

        var exception = Should.Throw<InvalidOperationException>(() => NotificationsHost.Create([], settings, environment: "Production"));
        exception.Message.ShouldContain("Email:Smtp:Host");

        // With a host (and public https links, DEC-350) it builds; Development and Testing may run without one.
        settings["Email:Smtp:Host"] = "mail.example.test";
        settings["Email:AppBaseUrl"] = "https://app.example.test/";
        using var app = NotificationsHost.Create([], settings, environment: "Production");
        app.ShouldNotBeNull();
        using var development = NotificationsHost.Create([], new Dictionary<string, string?>(settings) { ["Email:Smtp:Host"] = null }, environment: "Development");
        development.ShouldNotBeNull();
    }
}

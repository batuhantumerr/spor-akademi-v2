using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using SporAkademi.Application.Abstractions.Observability;
using SporAkademi.Infrastructure.Messaging;
using SporAkademi.IntegrationTests.Infrastructure;
using Testcontainers.RabbitMq;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>
/// The outbox with a real RabbitMQ (P5-03): a container of compose.yaml's image per test, on a host port that stays the
/// same when the test stops and starts the broker. Small retry delays so a dead letter is reached in seconds.
/// </summary>
public abstract partial class RabbitMqOutboxTest(DatabaseFixture database) : OutboxTest(database)
{
    protected const string DeadLetterQueue = "spor-akademi.events.dead-letter";
    protected const string ProbeQueue = "tests.probe.v1.ProbeHandler";

    private RabbitMqContainer _broker = null!;
    private MeterListener? _meters;
    private ActivityListener? _activities;

    protected Dictionary<string, int> Operations { get; } = [];

    protected List<Activity> Activities { get; } = [];

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            var settings = base.Settings;
            settings["ConnectionStrings:RabbitMq"] = _broker.GetConnectionString();
            settings["RabbitMq:RetryDelay"] = "00:00:01";
            settings["RabbitMq:MaxDeliveryAttempts"] = "3";
            settings["RabbitMq:PublishTimeout"] = "00:00:05";
            settings["RabbitMq:ConnectTimeout"] = "00:00:02";
            return settings;
        }
    }

    public override async ValueTask InitializeAsync()
    {
        var image = DatabaseFixture.ReadImage(RabbitImage());
        _broker = new RabbitMqBuilder(image).WithPortBinding(FreePort(), 5672).Build();
        await _broker.StartAsync(Ct);

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, AppTelemetry.MessagingMessages))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var operation = tags.ToArray().First(t => t.Key == "operation").Value?.ToString() ?? "?";
            lock (Operations)
            {
                Operations[operation] = Operations.GetValueOrDefault(operation) + (int)value;
            }
        });
        _meters.Start();

        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (Activities)
                {
                    Activities.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);

        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        _meters?.Dispose();
        _activities?.Dispose();
        await base.DisposeAsync();
        await _broker.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected Task StartBrokerAsync() => _broker.StartAsync(Ct);

    protected Task StopBrokerAsync() => _broker.StopAsync(Ct);

    protected int Count(string operation)
    {
        lock (Operations)
        {
            return Operations.GetValueOrDefault(operation);
        }
    }

    protected static async Task EventuallyAsync(Func<Task<bool>> condition, string what, int seconds = 60)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    protected static Task EventuallyAsync(Func<bool> condition, string what, int seconds = 60) =>
        EventuallyAsync(() => Task.FromResult(condition()), what, seconds);

    /// <summary>The messages waiting in a queue, asked through a connection of its own.</summary>
    protected async Task<uint> QueueDepthAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_broker.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        return (await channel.QueueDeclarePassiveAsync(queue, Ct)).MessageCount;
    }

    /// <summary>Takes one message off a queue to read its properties; the message is gone afterwards.</summary>
    protected async Task<BasicGetResult?> TakeAsync(string queue)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_broker.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);
        return await channel.BasicGetAsync(queue, autoAck: true, Ct);
    }

    protected async Task<int> InboxRowsAsync() =>
        await AcrossSchoolsAsync(db => db.Set<InboxMessage>().IgnoreQueryFilters().CountAsync(Ct));

    protected async Task ProcessUntilProcessedAsync()
    {
        // The client reconnects every few seconds after an outage; each round moves the fake clock past the postponement.
        await EventuallyAsync(async () =>
        {
            Api.Clock.Advance(TimeSpan.FromMinutes(5));
            await ProcessAsync();
            return (await SingleMessageAsync()).ProcessedAt is not null;
        }, "the outbox message to be published");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>rabbitmq:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RabbitImage();
}

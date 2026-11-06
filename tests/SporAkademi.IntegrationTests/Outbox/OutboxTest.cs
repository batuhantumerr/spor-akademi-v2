using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Outbox;

/// <summary>The API with a probe message type and handler, and small outbox limits so retries are quick to reach.</summary>
public abstract class OutboxTest(DatabaseFixture database) : IntegrationTest(database)
{
    protected static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(10);
    protected static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);
    protected static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);
    protected const int MaxAttempts = 3;
    protected const int BatchSize = 5;

    protected ProbeLog Probe { get; } = new();

    protected WebApplicationFactory<Program> Host { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected virtual Dictionary<string, string?> Settings => new()
    {
        ["Outbox:MaxAttempts"] = MaxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Outbox:BatchSize"] = BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Outbox:BaseRetryDelay"] = "00:00:10",
        ["Outbox:MaxRetryDelay"] = "00:01:00",
        ["Outbox:Retention"] = "1.00:00:00",
        ["Outbox:HandlerTimeout"] = "00:00:30",
    };

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Host = Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
            builder.ConfigureTestServices(services =>
            {
                services.AddOutboxMessageType(typeof(ProbeMessage));
                services.AddOutboxMessageType(typeof(OrphanMessage));
                services.AddSingleton(Probe);
                services.AddScoped<IOutboxMessageHandler<ProbeMessage>, ProbeHandler>();
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    internal OutboxProcessor Processor => Host.Services.GetRequiredService<OutboxProcessor>();

    protected Task<int> ProcessAsync() => Processor.ProcessBatchAsync(Ct);

    /// <summary>Runs <paramref name="operation"/> in a unit of work, like a command handler; it may enqueue with the given outbox.</summary>
    protected async Task<Result<int>> InUnitOfWorkAsync(Func<IOutbox, AppDbContext, CancellationToken, Task<Result<int>>> operation, Guid? actingTenant = null)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        if (actingTenant is not null)
        {
            // A school's request: the current user reports that school.
            scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(actingTenant);
        }

        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteInTransactionAsync(ct => operation(outbox, db, ct), Ct);
    }

    protected async Task EnqueueAsync(int count = 1, Guid? tenant = null)
    {
        var result = await InUnitOfWorkAsync((outbox, _, _) =>
        {
            for (var i = 0; i < count; i++)
            {
                outbox.Enqueue(new ProbeMessage(Guid.CreateVersion7(), $"probe {i}"), tenant);
            }

            return Task.FromResult(Result.Success(count));
        });
        result.IsSuccess.ShouldBeTrue();
    }

    internal async Task<List<OutboxMessage>> MessagesAsync()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AsNoTracking().OrderBy(m => m.Id).ToListAsync(Ct);
    }

    internal async Task<OutboxMessage> SingleMessageAsync() => (await MessagesAsync()).ShouldHaveSingleItem();
}

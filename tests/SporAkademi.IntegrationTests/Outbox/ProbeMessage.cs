using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Domain.Organization;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.IntegrationTests.Outbox;

public enum ProbeKind
{
    First,
    Second,
}

public enum ProbeHang
{
    None,

    /// <summary>Waits until its token is cancelled.</summary>
    Cooperative,

    /// <summary>Ignores its token and waits until <see cref="ProbeLog.Release"/>.</summary>
    IgnoresToken,
}

[OutboxMessage("tests.probe.v1")]
public sealed record ProbeMessage(Guid Value, string Label, ProbeKind Kind = ProbeKind.First) : IOutboxMessage;

/// <summary>Registered, but no handler exists for it.</summary>
[OutboxMessage("tests.orphan.v1")]
public sealed record OrphanMessage(Guid Value) : IOutboxMessage;

/// <param name="TenantId">The current user's tenant inside the handler.</param>
/// <param name="VisibleSportSchools">The schools whose sports the handler's tenant filter let through.</param>
public sealed record ProbeDelivery(Guid MessageId, Guid Value, int Attempt, Guid? TenantId, Guid[] VisibleSportSchools);

/// <summary>What the probe handler saw, and how it should behave; one instance per test.</summary>
public sealed class ProbeLog
{
    private int _failuresLeft;
    private int _running;
    private int _maxConcurrency;
    private int _killClaim;

    public ConcurrentQueue<ProbeDelivery> Deliveries { get; } = new();

    /// <summary>The handler throws this many times (across all messages) before succeeding.</summary>
    public void FailNext(int times) => _failuresLeft = times;

    public bool AlwaysFail { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public bool InspectTenant { get; set; }

    public int MaxConcurrency => _maxConcurrency;

    public ProbeHang Hang { get; set; }

    /// <summary>Completed when a handler starts.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets a handler that ignores its token finish.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Once: after handling, terminate the processor's claim connection, so its transaction never commits.</summary>
    public void KillClaimOnce() => _killClaim = 1;

    internal async Task HandleAsync(ProbeMessage message, OutboxMessageContext context, ICurrentUser user, AppDbContext db, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxConcurrency, running);
        try
        {
            Started.TrySetResult();
            if (Hang == ProbeHang.Cooperative)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (Hang == ProbeHang.IgnoresToken)
            {
                await Release.Task; // deliberately ignores the token
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            var schools = InspectTenant
                ? (await db.Set<Sport>().Select(s => s.TenantId).Distinct().ToListAsync(cancellationToken)).Select(s => s.Value).ToArray()
                : [];
            Deliveries.Enqueue(new ProbeDelivery(context.MessageId, message.Value, context.Attempt, user.TenantId, schools));

            if (Interlocked.Exchange(ref _killClaim, 0) == 1)
            {
                // The claim holds a row-share lock on the table; this handler's own connection is excluded.
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE relation = 'outbox.messages'::regclass AND pid <> pg_backend_pid()",
                    cancellationToken);
            }

            if (AlwaysFail || Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                throw new InvalidOperationException("Synthetic handler failure.");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

internal sealed class ProbeHandler(ProbeLog log, ICurrentUser user, AppDbContext db) : IOutboxMessageHandler<ProbeMessage>
{
    public Task Handle(ProbeMessage message, OutboxMessageContext context, CancellationToken cancellationToken) =>
        log.HandleAsync(message, context, user, db, cancellationToken);
}

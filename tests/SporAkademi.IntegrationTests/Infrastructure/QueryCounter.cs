using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// P4-08 (NFR-PRF-06): counts the SQL commands EF sends and the connections it opens while a measurement runs, for the
/// query count tests. Every opened connection also costs the row level security round trip (DEC-520, DEC-524), which is
/// not an EF command and so shows up only as an opening. Registered on every test API (ApiFactory); it records nothing
/// outside <see cref="MeasureAsync"/>. Tests run no background work (outbox and jobs are off), so what it sees is the
/// request's.
/// </summary>
public sealed class QueryCounter : DbCommandInterceptor, IDbConnectionInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();
    private int _openings;
    private volatile bool _active;

    /// <summary>Runs <paramref name="action"/> and returns the commands and connection openings it caused.</summary>
    public async Task<QueryCount> MeasureAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _commands.Clear();
        Interlocked.Exchange(ref _openings, 0);
        _active = true;
        try
        {
            await action();
        }
        finally
        {
            _active = false;
        }

        return new QueryCount([.. _commands], _openings);
    }

    public override DbCommand CommandInitialized(CommandEndEventData eventData, DbCommand result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_active)
        {
            _commands.Enqueue(result.CommandText);
        }

        return result;
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => CountOpening();

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        CountOpening();
        return Task.CompletedTask;
    }

    private void CountOpening()
    {
        if (_active)
        {
            Interlocked.Increment(ref _openings);
        }
    }
}

/// <param name="Commands">The SQL of every command EF ran, in order (the diagnostic when a count changes).</param>
/// <param name="Openings">Connections opened; each also ran the row level security <c>set_config</c>.</param>
public sealed record QueryCount(IReadOnlyList<string> Commands, int Openings)
{
    public override string ToString() =>
        $"{Commands.Count} commands, {Openings} connection openings:{Environment.NewLine}{string.Join(Environment.NewLine + "---" + Environment.NewLine, Commands)}";
}

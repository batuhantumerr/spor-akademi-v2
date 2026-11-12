using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Parents;

namespace SporAkademi.IntegrationTests.Realtime;

/// <summary>
/// The parent fixture plus SignalR clients (P3-08) against the in-memory test server. Long polling, because the test
/// server's handler serves plain HTTP; the hub, its authorization and its groups are the same for every transport.
/// </summary>
public abstract class RealtimeTest(DatabaseFixture database) : ParentTest(database)
{
    /// <summary>How long a test waits for a notice that must arrive, and for one that must not.</summary>
    protected static readonly TimeSpan Arrives = TimeSpan.FromSeconds(10);
    protected static readonly TimeSpan StaysQuiet = TimeSpan.FromMilliseconds(1500);

    private readonly List<Listener> _listeners = [];

    /// <summary>A started hub connection with <paramref name="token"/> that records every notice it receives.</summary>
    protected Task<Listener> ListenAsync(string token) => ListenAsync(Api, token);

    protected async Task<Listener> ListenAsync(WebApplicationFactory<Program> api, string token)
    {
        ArgumentNullException.ThrowIfNull(api);
        var listener = new Listener(Connection(api, token));
        _listeners.Add(listener);
        await listener.Connection.StartAsync(Ct);
        return listener;
    }

    protected static HubConnection Connection(WebApplicationFactory<Program> api, string token)
    {
        ArgumentNullException.ThrowIfNull(api);
        return new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners)
        {
            await listener.Connection.DisposeAsync();
            listener.Dispose();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>The notices one connection received, as the JSON the clients get (the JavaScript clients match <c>notify</c> case-insensitively).</summary>
    protected sealed class Listener : IDisposable
    {
        private readonly ConcurrentQueue<JsonElement> _received = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Listener(HubConnection connection)
        {
            Connection = connection;
            connection.On<JsonElement>("Notify", notice =>
            {
                _received.Enqueue(notice.Clone());
                _signal.Release();
            });
        }

        public HubConnection Connection { get; }

        public void Dispose() => _signal.Dispose();

        public IReadOnlyList<JsonElement> Received => [.. _received];

        /// <summary>Waits until a notice matching <paramref name="match"/> arrived, or fails after <paramref name="timeout"/>.</summary>
        public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var found = _received.Where(match).ToList();
                if (found.Count > 0)
                {
                    return found[0];
                }

                var left = timeout - watch.Elapsed;
                if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left, TestContext.Current.CancellationToken))
                {
                    throw new ShouldAssertException($"No matching notice within {timeout}; received: {string.Join(", ", _received.Select(r => r.GetRawText()))}");
                }
            }
        }
    }

    protected static Func<JsonElement, bool> OfType(string type) => n => n.GetProperty("type").GetString() == type;

    protected static string? Text(JsonElement notice, string property) =>
        notice.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

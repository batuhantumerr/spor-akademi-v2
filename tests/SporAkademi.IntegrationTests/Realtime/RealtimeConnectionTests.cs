using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SporAkademi.Api.Realtime;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Realtime;

/// <summary>
/// P3-08 connection lifecycle (DEC-399, DEC-400): a connection ends with its access token, and a failure while it is
/// being set up (e.g. the Valkey backplane is down) never keeps a slot of the per-user connection limit.
/// </summary>
[Trait("Story", "P3-08")]
public sealed class RealtimeConnectionTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connection_is_closed_when_its_access_token_expires()
    {
        var school = await TestSchools.CreateAsync(Api, "sure-okulu");
        // The test clock runs ahead of the machine's; the token's end is moved to a few seconds from now on the
        // machine's clock, which is what the server compares it with.
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, bearer =>
            {
                var validated = bearer.Events.OnTokenValidated;
                bearer.Events.OnTokenValidated = async context =>
                {
                    await validated(context);
                    if (context.HttpContext.Request.Headers.ContainsKey("X-Test-Short-Token"))
                    {
                        context.Properties.ExpiresUtc = TimeProvider.System.GetUtcNow().AddSeconds(3);
                    }
                };
            })));

        await using var shortLived = WebSocketConnection(api, Token(school), shortLived: true);
        await using var control = WebSocketConnection(api, Token(school), shortLived: false);
        var closed = new TaskCompletionSource();
        shortLived.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await shortLived.StartAsync(Ct);
        await control.StartAsync(Ct);
        shortLived.State.ShouldBe(HubConnectionState.Connected);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        shortLived.State.ShouldBe(HubConnectionState.Disconnected);
        control.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task A_connection_that_fails_while_joining_its_groups_gives_its_slot_back()
    {
        var school = await TestSchools.CreateAsync(Api, "kesinti-okulu");
        var failing = new FailingLifetimeManager();
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<HubLifetimeManager<NotificationHub>>(failing)));
        var token = Token(school, Guid.CreateVersion7());

        // More failed attempts than the limit allows connections.
        for (var i = 0; i < NotificationHub.MaxConnectionsPerUser + 3; i++)
        {
            await using var attempt = LongPollingConnection(api, token);
            var ended = new TaskCompletionSource();
            attempt.Closed += _ =>
            {
                ended.TrySetResult();
                return Task.CompletedTask;
            };
            try
            {
                // The handshake may complete before the hub's OnConnectedAsync fails; either way the connection ends.
                await attempt.StartAsync(Ct);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
            {
                ended.TrySetResult();
            }

            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        failing.Calls.ShouldBeGreaterThanOrEqualTo(NotificationHub.MaxConnectionsPerUser + 3);

        failing.Failing = false;
        await using var connection = LongPollingConnection(api, token);
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(Ct);
        // Over the limit, the hub would abort the connection right after the handshake.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        closed.Task.IsCompleted.ShouldBeFalse();
        connection.State.ShouldBe(HubConnectionState.Connected);
    }

    private string Token(Domain.Tenancy.SchoolId school, Guid? user = null) =>
        TestTokens.Create(user ?? Guid.CreateVersion7(), "Coach", school.Value, Api.Clock.GetUtcNow());

    private static HubConnection LongPollingConnection(WebApplicationFactory<Program> api, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    /// <summary>A WebSocket connection through the test server; the token in the query, as browsers send it.</summary>
    private static HubConnection WebSocketConnection(WebApplicationFactory<Program> api, string token, bool shortLived) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(api.Server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = api.Server.CreateWebSocketClient();
                    if (shortLived)
                    {
                        client.ConfigureRequest = request => request.Headers["X-Test-Short-Token"] = "1";
                    }

                    var uri = new UriBuilder(context.Uri) { Query = $"access_token={Uri.EscapeDataString(token)}" }.Uri;
                    return await client.ConnectAsync(uri, cancellationToken);
                };
            })
            .Build();

    /// <summary>The in-memory lifetime manager whose group joins fail on demand, like a backplane outage.</summary>
    private sealed class FailingLifetimeManager : HubLifetimeManager<NotificationHub>
    {
        private readonly DefaultHubLifetimeManager<NotificationHub> _inner = new(NullLogger<DefaultHubLifetimeManager<NotificationHub>>.Instance);

        private int _calls;

        public bool Failing { get; set; } = true;

        public int Calls => _calls;

        public override Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Failing ? throw new InvalidOperationException("Backplane unavailable.") : _inner.AddToGroupAsync(connectionId, groupName, cancellationToken);
        }

        public override Task OnConnectedAsync(HubConnectionContext connection) => _inner.OnConnectedAsync(connection);

        public override Task OnDisconnectedAsync(HubConnectionContext connection) => _inner.OnDisconnectedAsync(connection);

        public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _inner.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);

        public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendAllAsync(methodName, args, cancellationToken);

        public override Task SendAllExceptAsync(string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            _inner.SendAllExceptAsync(methodName, args, excludedConnectionIds, cancellationToken);

        public override Task SendConnectionAsync(string connectionId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendConnectionAsync(connectionId, methodName, args, cancellationToken);

        public override Task SendConnectionsAsync(IReadOnlyList<string> connectionIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendConnectionsAsync(connectionIds, methodName, args, cancellationToken);

        public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendGroupAsync(groupName, methodName, args, cancellationToken);

        public override Task SendGroupExceptAsync(string groupName, string methodName, object?[] args, IReadOnlyList<string> excludedConnectionIds, CancellationToken cancellationToken = default) =>
            _inner.SendGroupExceptAsync(groupName, methodName, args, excludedConnectionIds, cancellationToken);

        public override Task SendGroupsAsync(IReadOnlyList<string> groupNames, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendGroupsAsync(groupNames, methodName, args, cancellationToken);

        public override Task SendUserAsync(string userId, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendUserAsync(userId, methodName, args, cancellationToken);

        public override Task SendUsersAsync(IReadOnlyList<string> userIds, string methodName, object?[] args, CancellationToken cancellationToken = default) =>
            _inner.SendUsersAsync(userIds, methodName, args, cancellationToken);
    }
}

using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>
/// P4-11 (DEC-593, DEC-594): logout racing a grace replay of the same refresh token (DEC-446). A gate on the user
/// repository holds one request right after it loaded the user and its tokens, so the other request commits in between
/// and the order is forced, not hoped for. In both orders the logout wins: the replay hands out no usable token, the
/// whole family is revoked and the user's other devices keep their sessions.
/// </summary>
[Trait("Story", "P4-11")]
public sealed class LogoutGraceRaceTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string Email = "coach@example.com";

    [Fact]
    public async Task A_logout_committed_while_a_grace_replay_is_running_makes_the_replay_fail_without_a_token()
    {
        var coach = await SeedUserAsync(Email, UserRole.Coach, await SeedSchoolAsync());
        var otherDevice = await LoginOkAsync(Email);
        var gate = new LoadGate();
        using var api = GatedApi(gate);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var stale = await StaleTokenAsync(client);

        // The replay has loaded the family (its successor still unused, so it qualifies) and waits; logout commits.
        var held = gate.HoldNextLoad();
        var replay = SendAsync(client, "refresh", stale);
        await held.Loaded;
        (await SendAsync(client, "logout", stale)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        held.Release();

        var replayed = await replay;
        replayed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(replayed)).ShouldBe("Concurrency.Conflict");
        RefreshCookieOf(replayed).ShouldBeNull();
        await FamilyIsRevokedAsync(coach, gate, stale, tokens: 2);

        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The client's single retry after the 409 (DEC-447) gets no token either. The stale token's family is closed, so
        // it no longer qualifies for grace and counts as a reuse: DEC-446 ends every session of the user (DEC-594).
        var retry = await SendAsync(client, "refresh", stale);
        retry.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(retry)).ShouldBe("Auth.SessionExpired");
        RefreshCookieOf(retry)?.Value.ShouldBeEmpty();
        (await LoadUserAsync(coach.Id)).RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
    }

    [Fact]
    public async Task A_grace_replay_committed_while_a_logout_is_running_is_revoked_by_the_logout()
    {
        var coach = await SeedUserAsync(Email, UserRole.Coach, await SeedSchoolAsync());
        var otherDevice = await LoginOkAsync(Email);
        var gate = new LoadGate();
        using var api = GatedApi(gate);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var stale = await StaleTokenAsync(client);

        // Logout has loaded the family and waits; the replay commits a new token in it.
        var held = gate.HoldNextLoad();
        var logout = SendAsync(client, "logout", stale);
        await held.Loaded;
        var replayed = await SendAsync(client, "refresh", stale);
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var reissued = RefreshCookieOf(replayed)!.Value;
        held.Release();

        // The logout's stale save conflicts; its one retry (DEC-446) sees the new token and revokes it with the family.
        (await logout).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await FamilyIsRevokedAsync(coach, gate, stale, tokens: 3);
        var afterLogout = await SendAsync(client, "refresh", reissued);
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        RefreshCookieOf(afterLogout)?.Value.ShouldBeEmpty();
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> GatedApi(LoadGate gate) =>
        Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var repository = services.Single(d => d.ServiceType == typeof(IUserRepository)).ImplementationType!;
            services.Replace(ServiceDescriptor.Scoped(sp =>
                GatedUserRepository.Wrap((IUserRepository)ActivatorUtilities.CreateInstance(sp, repository), gate)));
        }));

    /// <summary>Logs in and rotates once; the rotation's answer is "lost", so the login's token is a stale one within the grace window.</summary>
    private static async Task<string> StaleTokenAsync(HttpClient client)
    {
        var login = await client.PostAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { email = Email, password = OwnPassword }),
            TestContext.Current.CancellationToken);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stale = RefreshCookieOf(login)!.Value;
        (await SendAsync(client, "refresh", stale)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return stale;
    }

    private async Task FamilyIsRevokedAsync(User coach, LoadGate gate, string stale, int tokens)
    {
        var all = (await LoadUserAsync(coach.Id)).RefreshTokens;
        var family = all.Single(t => t.TokenHash == gate.Hash(stale)).FamilyId;
        var members = all.Where(t => t.FamilyId == family).ToList();
        members.Count.ShouldBe(tokens);
        members.ShouldAllBe(t => t.RevokedAt != null);
        all.Count(t => t.FamilyId != family && t.RevokedAt == null).ShouldBeGreaterThan(0, "the other sessions stay");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string action, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/auth/{action}");
        request.Headers.Add("Cookie", $"sa_refresh={token}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Holds the first refresh-token lookup after <see cref="HoldNextLoad"/> until the test releases it.</summary>
    private sealed class LoadGate
    {
        private readonly SporAkademi.Infrastructure.Identity.RefreshTokenFactory _hasher = new();
        private Hold? _next;

        public string Hash(string token) => _hasher.Hash(token);

        public Hold HoldNextLoad() => _next = new Hold();

        public async Task<User?> AfterLoadAsync(Task<User?> load)
        {
            var user = await load;
            if (Interlocked.Exchange(ref _next, null) is { } hold)
            {
                hold.SignalLoaded();
                await hold.Released.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return user;
        }
    }

    private sealed class Hold
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Loaded => _loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task Released => _released.Task;

        public void SignalLoaded() => _loaded.TrySetResult();

        public void Release() => _released.TrySetResult();
    }

    /// <summary>The real repository; its refresh-token lookup goes through the <see cref="LoadGate"/>.</summary>
    public class GatedUserRepository : DispatchProxy
    {
        private IUserRepository _inner = null!;
        private object _gate = null!;

        internal static IUserRepository Wrap(IUserRepository inner, object gate)
        {
            var proxy = Create<IUserRepository, GatedUserRepository>();
            var gated = (GatedUserRepository)(object)proxy;
            gated._inner = inner;
            gated._gate = gate;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            object? result;
            try
            {
                result = targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                // The repository's own exception, not the reflection wrapper (e.g. a cancellation stays a cancellation).
                ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }

            return targetMethod.Name == nameof(IUserRepository.GetByRefreshTokenHashAsync)
                ? ((LoadGate)_gate).AfterLoadAsync((Task<User?>)result!)
                : result;
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Sessions;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>Findings of the security review of the identity endpoints.</summary>
public sealed class AuthHardeningTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string NewPassword = "Yeni-Sifrem-2027";

    private Task<HttpResponseMessage> ChangePasswordAsync(string accessToken, string current, string next) =>
        SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", accessToken, new { currentPassword = current, newPassword = next });

    [Fact]
    [Trait("Story", "US-05")]
    public async Task New_password_must_differ_from_the_current_one()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        var response = await ChangePasswordAsync(session.AccessToken, OwnPassword, OwnPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
        problem.GetProperty("errors").GetProperty("newPassword")[0].GetString().ShouldBe("Auth.PasswordUnchanged");
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Wrong_current_passwords_count_towards_the_lockout()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");

        for (var i = 0; i < 4; i++)
        {
            (await ErrorCodeAsync(await ChangePasswordAsync(session.AccessToken, "Yanlis-Sifre-123", NewPassword)))
                .ShouldBe("Auth.CurrentPasswordIncorrect");
        }

        (await ErrorCodeAsync(await ChangePasswordAsync(session.AccessToken, "Yanlis-Sifre-123", NewPassword))).ShouldBe("Auth.AccountLocked");
        // Locked: neither the right current password nor a login gets through.
        (await ErrorCodeAsync(await ChangePasswordAsync(session.AccessToken, OwnPassword, NewPassword))).ShouldBe("Auth.AccountLocked");
        (await ErrorCodeAsync(await LoginAsync("coach@example.com", OwnPassword))).ShouldBe("Auth.AccountLocked");

        Api.Clock.Advance(TimeSpan.FromMinutes(15));
        var unlocked = await LoginOkAsync("coach@example.com");
        (await ChangePasswordAsync(unlocked.AccessToken, OwnPassword, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [Trait("Story", "US-04")]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/change-password")]
    public async Task Refresh_and_change_password_are_rate_limited_per_client(string path)
    {
        for (var i = 0; i < 10; i++)
        {
            (await PostXhrAsync(path)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        (await PostXhrAsync(path)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task Each_auth_endpoint_has_its_own_rate_limit_budget()
    {
        for (var i = 0; i < 10; i++)
        {
            await PostXhrAsync("/api/v1/auth/refresh");
        }

        (await LoginAsync("nobody@example.com", "Yanlis-Sifre-123")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task Logout_clears_the_cookie_even_when_revoking_fails()
    {
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<ICommandHandler<LogoutCommand, Unit>, ConflictingLogoutHandler>())));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", "sa_refresh=c29tZS10b2tlbg");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        RefreshCookieOf(response)!.Attributes["expires"].ShouldContain("1970");
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Api_responses_are_never_cached()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);

        var login = await LoginAsync("admin@example.com", OwnPassword);
        var failedLogin = await LoginAsync("admin@example.com", "Yanlis-Sifre-123");
        var admin = await SessionFromAsync(login);
        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{coach.Id.Value}/reset-password", admin.AccessToken);

        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var response in new[] { login, failedLogin, reset })
        {
            response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        }
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task Concurrent_refreshes_with_the_same_token_rotate_it_only_once()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        using var factory = new BarrierRefreshTokenFactory();
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IRefreshTokenFactory>(factory))));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "coach@example.com", password = OwnPassword }, TestContext.Current.CancellationToken);
        var token = RefreshCookieOf(login)!.Value;

        // Both requests have loaded the token before either rotates it.
        factory.Enable(participants: 2);
        var responses = await Task.WhenAll(RefreshWithAsync(client, token), RefreshWithAsync(client, token));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        (await ErrorCodeAsync(conflict)).ShouldBe("Concurrency.Conflict");
        RefreshCookieOf(conflict).ShouldBeNull();
        var tokens = (await LoadUserAsync(coach.Id)).RefreshTokens;
        var rotated = tokens.Single(t => t.ReplacedByTokenId is not null);
        tokens.Count(t => t.FamilyId == rotated.FamilyId && t.RevokedAt is null).ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task Concurrent_grace_replays_conflict_once_and_a_retry_still_succeeds_without_revoking()
    {
        // DEC-446: two tabs boot after a lost refresh response and both present the stale cookie at once.
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        using var factory = new BarrierRefreshTokenFactory();
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IRefreshTokenFactory>(factory))));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var otherDevice = await LoginOkAsync("coach@example.com");
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "coach@example.com", password = OwnPassword }, TestContext.Current.CancellationToken);
        var stale = RefreshCookieOf(login)!.Value;
        (await RefreshWithAsync(client, stale)).StatusCode.ShouldBe(HttpStatusCode.OK); // this response is "lost"

        factory.Enable(participants: 2);
        var responses = await Task.WhenAll(RefreshWithAsync(client, stale), RefreshWithAsync(client, stale));
        factory.Disable();

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await ErrorCodeAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict))).ShouldBe("Concurrency.Conflict");
        var retry = await RefreshWithAsync(client, stale);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(otherDevice.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var family = (await LoadUserAsync(coach.Id)).RefreshTokens.Single(t => t.TokenHash == factory.Hash(stale)).FamilyId;
        (await LoadUserAsync(coach.Id)).RefreshTokens.Count(t => t.FamilyId == family && t.RevokedAt is null).ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task A_grace_replay_racing_the_successors_rotation_conflicts_exactly_once_and_revokes_nothing()
    {
        var school = await SeedSchoolAsync();
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        using var factory = new BarrierRefreshTokenFactory();
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IRefreshTokenFactory>(factory))));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "coach@example.com", password = OwnPassword }, TestContext.Current.CancellationToken);
        var stale = RefreshCookieOf(login)!.Value;
        var successor = RefreshCookieOf(await RefreshWithAsync(client, stale))!.Value;

        factory.Enable(participants: 2);
        var responses = await Task.WhenAll(RefreshWithAsync(client, stale), RefreshWithAsync(client, successor));
        factory.Disable();

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        (await ErrorCodeAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict))).ShouldBe("Concurrency.Conflict");
        var tokens = (await LoadUserAsync(coach.Id)).RefreshTokens;
        var family = tokens.Single(t => t.TokenHash == factory.Hash(stale)).FamilyId;
        tokens.Count(t => t.FamilyId == family && t.RevokedAt is null).ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-04")]
    public async Task Logout_is_retried_once_after_a_concurrency_conflict()
    {
        var handler = new ConflictOnceLogoutHandler();
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ICommandHandler<LogoutCommand, Unit>>(handler))));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", "sa_refresh=c29tZS10b2tlbg");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    [Trait("Story", "US-05")]
    public async Task Admin_reset_lifts_the_lock_ends_sessions_and_requires_a_password_change()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("admin@example.com", UserRole.SchoolAdmin, school);
        var coach = await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var coachSession = await LoginOkAsync("coach@example.com");
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync("coach@example.com", "Yanlis-Sifre-123");
        }

        var admin = await LoginOkAsync("admin@example.com");
        var reset = await SendAsync(HttpMethod.Post, $"/api/v1/coaches/{coach.Id.Value}/reset-password", admin.AccessToken);
        var temporary = (await reset.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("temporaryPassword").GetString()!;

        (await RefreshAsync(coachSession.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var afterReset = await LoginOkAsync("coach@example.com", temporary);
        afterReset.Body.User.MustChangePassword.ShouldBeTrue();
        Decode(afterReset.AccessToken).Claims.Single(c => c.Type == "pwd_change_required").Value.ShouldBe("true");
    }

    [Fact]
    [Trait("Story", "US-05")]
    public async Task Refresh_keeps_the_password_change_requirement()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school, temporary: true);
        var session = await LoginOkAsync("coach@example.com", TemporaryPassword);

        var refreshed = await SessionFromAsync(await RefreshAsync(session.RefreshToken));

        Decode(refreshed.AccessToken).Claims.Single(c => c.Type == "pwd_change_required").Value.ShouldBe("true");
        refreshed.Body.User.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Refresh_of_a_deactivated_school_deletes_the_cookie()
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, school);
        var session = await LoginOkAsync("coach@example.com");
        await ExecuteSqlAsync("UPDATE tenancy.schools SET status = 'Deactivated'");

        var response = await RefreshAsync(session.RefreshToken);

        (await ErrorCodeAsync(response)).ShouldBe("Auth.AccountInactive");
        var cleared = RefreshCookieOf(response)!;
        cleared.Value.ShouldBeEmpty();
        cleared.Attributes["expires"].ShouldContain("1970");
    }

    private async Task<HttpResponseMessage> PostXhrAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> RefreshWithAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"sa_refresh={token}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class ConflictingLogoutHandler : ICommandHandler<LogoutCommand, Unit>
    {
        public Task<Result<Unit>> Handle(LogoutCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure<Unit>(PersistenceErrors.LockTimeout));
    }

    /// <summary>Fails the first logout with a concurrency conflict (a grace replay won the race), then succeeds.</summary>
    private sealed class ConflictOnceLogoutHandler : ICommandHandler<LogoutCommand, Unit>
    {
        private int _calls;

        public int Calls => _calls;

        public Task<Result<Unit>> Handle(LogoutCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1
                ? Result.Failure<Unit>(PersistenceErrors.ConcurrencyConflict)
                : Result.Success(Unit.Value));
    }

    /// <summary>The real factory; once enabled, token creation (which happens after the user is loaded) waits for all requests.</summary>
    private sealed class BarrierRefreshTokenFactory : IRefreshTokenFactory, IDisposable
    {
        private readonly RefreshTokenFactory _inner = new();
        private Barrier? _barrier;

        public void Enable(int participants) => _barrier = new Barrier(participants);

        public void Disable() => _barrier = null;

        public NewRefreshToken Create()
        {
            _barrier?.SignalAndWait(TimeSpan.FromSeconds(10)).ShouldBeTrue("The concurrent requests did not meet.");
            return _inner.Create();
        }

        public string Hash(string token) => _inner.Hash(token);

        public void Dispose() => _barrier?.Dispose();
    }
}

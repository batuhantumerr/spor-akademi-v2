using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Identity;

/// <summary>Seeds schools and users directly and drives the auth endpoints over HTTP, handling the refresh cookie by hand.</summary>
public abstract class IdentityTest(DatabaseFixture database) : IntegrationTest(database)
{
    public const string TemporaryPassword = "Gecici-Sifre-2027";
    public const string OwnPassword = "Kendi-Sifrem-2027";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A client without a cookie jar: the refresh cookie is Secure, so tests read and send it explicitly.</summary>
    protected HttpClient Http { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Http = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public override async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected Task<SchoolId> SeedSchoolAsync(string slug = "kadikoy-spor") =>
        InScopeAsync(async sp =>
        {
            var school = School.Create("Kadıköy Spor", Slug.Create(slug).Value, "Europe/Istanbul", RetentionPolicy.Default).Value;
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(school);
            await db.SaveChangesAsync();
            return school.Id;
        });

    /// <summary>A user whose password is <see cref="OwnPassword"/>, or <see cref="TemporaryPassword"/> when <paramref name="temporary"/>.</summary>
    protected Task<User> SeedUserAsync(string email, UserRole role, SchoolId? school, bool temporary = false) =>
        InScopeAsync(async sp =>
        {
            var hasher = sp.GetRequiredService<IPasswordHasher>();
            var user = User.Create(school, Email.Create(email).Value, hasher.Hash(TemporaryPassword), role, "Deniz", "Kaya", null).Value;
            if (!temporary)
            {
                user.ChangePassword(hasher.Hash(OwnPassword), sp.GetRequiredService<IRefreshTokenFactory>().Create().Hash, Api.Clock.GetUtcNow());
            }

            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(user);
            await db.SaveChangesAsync();
            return user;
        });

    protected Task<User> LoadUserAsync(UserId id) =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<User>().AsNoTracking()
            .Include(u => u.RefreshTokens).SingleAsync(u => u.Id == id));

    protected Task ExecuteSqlAsync(string sql) =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql));

    protected Task DeactivateUserAsync(UserId id) =>
        InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var user = await db.Set<User>().Include(u => u.RefreshTokens).SingleAsync(u => u.Id == id);
            user.Deactivate(Api.Clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync();
        });

    protected Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        Http.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, TestContext.Current.CancellationToken);

    /// <summary>Logs in and returns the access token and the refresh cookie value.</summary>
    protected async Task<Session> LoginOkAsync(string email, string password = OwnPassword)
    {
        var response = await LoginAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await SessionFromAsync(response);
    }

    protected Task<HttpResponseMessage> RefreshAsync(string? refreshToken, bool withHeader = true) =>
        SendWithCookieAsync("/api/v1/auth/refresh", refreshToken, withHeader);

    protected Task<HttpResponseMessage> LogoutAsync(string? refreshToken, bool withHeader = true) =>
        SendWithCookieAsync("/api/v1/auth/logout", refreshToken, withHeader);

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    protected static async Task<Session> SessionFromAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<SessionBody>(Json, TestContext.Current.CancellationToken);
        var cookie = RefreshCookieOf(response);
        cookie.ShouldNotBeNull();
        return new Session(body!.AccessToken, cookie.Value, body);
    }

    protected static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The <c>sa_refresh</c> Set-Cookie of a response, parsed into value and attributes.</summary>
    protected static SetCookie? RefreshCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var header = values.SingleOrDefault(v => v.StartsWith("sa_refresh=", StringComparison.Ordinal));
        if (header is null)
        {
            return null;
        }

        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var attributes = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : string.Empty, StringComparer.Ordinal);
        return new SetCookie(parts[0]["sa_refresh=".Length..], attributes);
    }

    protected static JwtSecurityToken Decode(string accessToken) => new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

    private async Task<HttpResponseMessage> SendWithCookieAsync(string path, string? refreshToken, bool withHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"sa_refresh={refreshToken}");
        }

        if (withHeader)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public sealed record Session(string AccessToken, string RefreshToken, SessionBody Body);

    public sealed record SessionBody(string AccessToken, int ExpiresIn, UserBody User);

    public sealed record UserBody(Guid Id, string Email, string FirstName, string LastName, string Role, Guid? TenantId, bool MustChangePassword);

    public sealed record SetCookie(string Value, IReadOnlyDictionary<string, string> Attributes);
}

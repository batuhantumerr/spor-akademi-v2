using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Jobs;

/// <summary>
/// The job panel (DEC-332): only an active Platform Admin, through a panel session cookie minted by an
/// authenticated API call, may open <c>/jobs</c>. Bearer tokens, other roles, expired, replaced and forged
/// sessions are refused.
/// </summary>
[Trait("Story", "P2-F2")]
public sealed class JobsDashboardTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string StartSession = "/api/v1/platform/jobs/dashboard-session";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_requests_get_401()
    {
        (await Http.GetAsync(new Uri("/jobs", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Http.GetAsync(new Uri("/jobs/recurring", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Http.PostAsync(new Uri(StartSession, UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(UserRole.SchoolAdmin)]
    [InlineData(UserRole.Coach)]
    public async Task School_users_cannot_start_a_session_nor_open_the_panel_with_their_token(UserRole role)
    {
        var school = await SeedSchoolAsync();
        await SeedUserAsync("user@example.com", role, school);
        var session = await LoginOkAsync("user@example.com");

        var start = await SendAsync(HttpMethod.Post, StartSession, session.AccessToken);
        start.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        start.Headers.Contains("Set-Cookie").ShouldBeFalse();

        // The panel accepts no bearer token at all.
        (await SendAsync(HttpMethod.Get, "/jobs", session.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_platform_admin_gets_a_short_lived_strict_cookie_and_opens_the_panel_with_it_only()
    {
        var token = await PlatformAdminTokenAsync();

        // The token alone does not open the panel.
        (await SendAsync(HttpMethod.Get, "/jobs", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var start = await SendAsync(HttpMethod.Post, StartSession, token);
        start.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        start.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var cookie = PanelCookieOf(start);
        cookie.Value.Length.ShouldBe(43); // 256 bits, base64url
        cookie.Attributes["path"].ShouldBe("/jobs");
        cookie.Attributes["max-age"].ShouldBe("900");
        cookie.Attributes["samesite"].ShouldBe("strict");
        cookie.Attributes.ShouldContainKey("secure");
        cookie.Attributes.ShouldContainKey("httponly");

        var panel = await OpenAsync("/jobs", cookie.Value);
        panel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await panel.Content.ReadAsStringAsync(Ct)).ShouldContain("Spor Akademi");
        panel.Headers.CacheControl!.NoStore.ShouldBeTrue();
        panel.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        (await OpenAsync("/jobs/recurring", cookie.Value)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_cookie_stops_working_when_it_expires()
    {
        var cookie = await StartPanelSessionAsync(await PlatformAdminTokenAsync());
        Api.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Api.Clock.Advance(TimeSpan.FromSeconds(1));

        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Only a new call from the admin app opens the panel again.
        (await OpenAsync("/jobs", await StartPanelSessionAsync(await LoginTokenAsync()))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_new_session_ends_the_previous_one_and_a_forged_cookie_is_refused()
    {
        var token = await PlatformAdminTokenAsync();
        var first = await StartPanelSessionAsync(token);
        var second = await StartPanelSessionAsync(token);

        (await OpenAsync("/jobs", first)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await OpenAsync("/jobs", second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await OpenAsync("/jobs", second[..^2] + "AA")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_deactivated_platform_admin_loses_the_panel_at_once()
    {
        var admin = await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var cookie = await StartPanelSessionAsync((await LoginOkAsync("platform@example.com")).AccessToken);

        await DeactivateUserAsync(admin.Id);

        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logging_out_ends_the_panel_session_but_refreshing_the_token_does_not()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var session = await LoginOkAsync("platform@example.com");
        var cookie = await StartPanelSessionAsync(session.AccessToken);

        var refreshed = await RefreshAsync(session.RefreshToken);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await LogoutAsync(RefreshCookieOf(refreshed)!.Value)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_the_password_ends_the_panel_session()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var session = await LoginOkAsync("platform@example.com");
        var cookie = await StartPanelSessionAsync(session.AccessToken);

        var changed = await SendAsync(HttpMethod.Post, "/api/v1/auth/change-password", session.AccessToken, new { currentPassword = OwnPassword, newPassword = "Yepyeni-Sifre-2027" });
        changed.IsSuccessStatusCode.ShouldBeTrue();

        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Story", "P2-F4")]
    public async Task Setting_a_new_password_with_a_reset_link_ends_the_panel_session()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        var session = await LoginOkAsync("platform@example.com");
        var cookie = await StartPanelSessionAsync(session.AccessToken);
        (await Http.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "platform@example.com" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        var token = CapturingEmailSender.TokenIn(Api.Emails.Sent.ShouldHaveSingleItem());
        (await Http.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = "Yepyeni-Sifre-2027" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await OpenAsync("/jobs", cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_panel_is_read_only_by_default_even_with_a_valid_antiforgery_token()
    {
        var cookie = await StartPanelSessionAsync(await PlatformAdminTokenAsync());
        var page = await OpenAsync("/jobs/recurring", cookie);
        (await page.Content.ReadAsStringAsync(Ct)).ShouldNotContain("/jobs/recurring/trigger");

        var trigger = await TriggerAsync(Http, cookie, await AntiforgeryOfAsync(page));

        ((int)trigger.StatusCode).ShouldBeOneOf(401, 403);
    }

    [Fact]
    public async Task With_actions_on_a_post_needs_the_antiforgery_token()
    {
        await using var host = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Dashboard:ReadOnly"] = "false" })));
        using var http = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var token = await PlatformAdminTokenAsync(); // signed with the same key, so the other host accepts it
        using var start = new HttpRequestMessage(HttpMethod.Post, StartSession);
        start.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var cookie = PanelCookieOf(await http.SendAsync(start, Ct)).Value;
        using var open = new HttpRequestMessage(HttpMethod.Get, "/jobs/recurring");
        open.Headers.Add("Cookie", $"sa_jobs={cookie}");
        var antiforgery = await AntiforgeryOfAsync(await http.SendAsync(open, Ct));

        (await TriggerAsync(http, cookie, null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TriggerAsync(http, cookie, antiforgery with { Token = antiforgery.Token[..^2] + "AA" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ((int)(await TriggerAsync(http, cookie, antiforgery)).StatusCode).ShouldBeLessThan(300);
    }

    private async Task<string> LoginTokenAsync() => (await LoginOkAsync("platform@example.com")).AccessToken;

    private static async Task<HttpResponseMessage> TriggerAsync(HttpClient http, string cookie, Antiforgery? antiforgery)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/jobs/recurring/trigger")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["jobs[]"] = "outbox-process" }),
        };
        var cookies = $"sa_jobs={cookie}";
        if (antiforgery is not null)
        {
            cookies += $"; sa_jobs_af={antiforgery.Cookie}";
            request.Headers.Add("X-Jobs-Antiforgery", antiforgery.Token);
        }

        request.Headers.Add("Cookie", cookies);
        return await http.SendAsync(request, Ct);
    }

    /// <summary>The antiforgery cookie and the request token the panel page carries in its csrf-token meta tag.</summary>
    private static async Task<Antiforgery> AntiforgeryOfAsync(HttpResponseMessage page)
    {
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var token = System.Text.RegularExpressions.Regex.Match(html, """<meta name="csrf-token" content="(?<token>[^"]+)">""").Groups["token"].Value;
        var cookie = page.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("sa_jobs_af=", StringComparison.Ordinal));
        token.ShouldNotBeNullOrEmpty();
        return new Antiforgery(cookie.Split(';')[0]["sa_jobs_af=".Length..], token);
    }

    private sealed record Antiforgery(string Cookie, string Token);

    private async Task<string> PlatformAdminTokenAsync()
    {
        await SeedUserAsync("platform@example.com", UserRole.PlatformAdmin, null);
        return (await LoginOkAsync("platform@example.com")).AccessToken;
    }

    private async Task<string> StartPanelSessionAsync(string accessToken)
    {
        var response = await SendAsync(HttpMethod.Post, StartSession, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return PanelCookieOf(response).Value;
    }

    private async Task<HttpResponseMessage> OpenAsync(string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"sa_jobs={cookie}");
        return await Http.SendAsync(request, Ct);
    }

    private static SetCookie PanelCookieOf(HttpResponseMessage response)
    {
        var header = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("sa_jobs=", StringComparison.Ordinal));
        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var attributes = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : string.Empty, StringComparer.Ordinal);
        return new SetCookie(parts[0]["sa_jobs=".Length..], attributes);
    }
}

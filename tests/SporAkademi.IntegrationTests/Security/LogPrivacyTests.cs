using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// NFR-PRV-03: log events carry ids and codes only. Representative requests run with every log level on and a
/// collecting sink; no e-mail, phone, name, birth date, password or token value may appear in any event.
/// </summary>
public sealed class LogPrivacyTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string Email = "gizli.kullanici@example.com";
    private const string StudentFirstName = "Gizliadli";
    private const string StudentLastName = "Saklisoyadli";
    private const string GuardianFirstName = "Velisakli";
    private const string StudentEmail = "ogrenci.gizli@example.com";
    private const string GuardianEmail = "veli.gizli@example.com";
    private const string BirthDate = "2015-07-23";
    private const string StudentPhone = "0532 987 65 43";
    private const string EmergencyPhone = "0533 876 54 32";
    private const string GuardianPhone = "0534 765 43 21";

    [Fact]
    public async Task Logs_never_contain_personal_data_passwords_or_tokens()
    {
        var sink = new CollectingSink();
        using var api = Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Verbose",
            }));
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(sink));
        });
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var ct = TestContext.Current.CancellationToken;

        var school = await TestSchools.CreateAsync(Api, "gizlilik-okulu");
        await SeedUserAsync(Email, UserRole.SchoolAdmin, school);

        // Failed and successful login.
        var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = "Yanlis-Sifre-987" }, ct);
        failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = OwnPassword }, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // Refresh with the cookie, so the refresh token passes through the pipeline too.
        using (var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh"))
        {
            refresh.Headers.Add("Cookie", $"sa_refresh={session.RefreshToken}");
            refresh.Headers.Add("X-Requested-With", "XMLHttpRequest");
            (await client.SendAsync(refresh, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A student with every kind of personal data, and a rejected one (validation failure path).
        var student = new
        {
            firstName = StudentFirstName,
            lastName = StudentLastName,
            birthDate = BirthDate,
            gender = "Female",
            phone = StudentPhone,
            email = StudentEmail,
            emergencyContact = new { name = "Acilkisi Gizlisoyad", phone = EmergencyPhone, relationship = "Teyze" },
            guardians = new[]
            {
                new { newGuardian = new { firstName = GuardianFirstName, lastName = StudentLastName, phone = GuardianPhone, email = GuardianEmail }, relationship = "Mother" },
            },
        };
        (await client.PostAsJsonAsync("/api/v1/students", student, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/v1/students", student with { }, ct)).StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
        (await client.PostAsJsonAsync("/api/v1/students", new { firstName = StudentFirstName, email = "bozuk-eposta" }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(new Uri($"/api/v1/students?search={StudentLastName}", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Password change (old and new passwords in the body) and a wrong current password.
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Yanlis-Sifre-987", newPassword = "Gizli-Yeni-Sifre-55" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var changed = await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = OwnPassword, newPassword = "Gizli-Yeni-Sifre-55" }, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newSession = await SessionFromAsync(changed);

        var events = sink.Rendered();
        // The test must see real request logs, otherwise it proves nothing.
        events.ShouldContain(e => e.Contains("LoginCommand", StringComparison.Ordinal) && e.Contains("Auth.InvalidCredentials", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("RegisterStudentCommand", StringComparison.Ordinal));

        string[] forbidden =
        [
            Email, "gizli.kullanici",
            StudentEmail, GuardianEmail,
            StudentFirstName, StudentLastName, GuardianFirstName, "Acilkisi", "Gizlisoyad",
            BirthDate, "23.07.2015",
            "9876543", "987 65 43", "8765432", "7654321",
            OwnPassword, "Yanlis-Sifre-987", "Gizli-Yeni-Sifre-55",
            session.AccessToken, session.RefreshToken, newSession.AccessToken, newSession.RefreshToken,
        ];
        foreach (var value in forbidden)
        {
            events.Where(e => e.Contains(value, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty($"'{value}' leaked into the logs.");
        }
    }

    /// <summary>Keeps every event rendered with its message, properties and exception.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            var properties = string.Join(" ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}"));
            _events.Enqueue($"{logEvent.Level} {logEvent.RenderMessage(CultureInfo.InvariantCulture)} {properties} {logEvent.Exception}");
        }

        public IReadOnlyList<string> Rendered() => [.. _events];
    }
}

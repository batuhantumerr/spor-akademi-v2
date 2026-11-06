using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
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

    /// <summary>Free text an admin might write; it can name people, so it must not reach the logs either.</summary>
    private const string CancelReason = "Saklisoyadli ailesi yanlış aya ödedi";

    [Fact]
    public async Task Logs_never_contain_personal_data_passwords_or_tokens()
    {
        var sink = new CollectingSink();
        using var api = CollectingApi(sink);
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
        var registered = await client.PostAsJsonAsync("/api/v1/students", student, ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var studentId = await IdOfAsync(registered);
        (await client.PostAsJsonAsync("/api/v1/students", student with { }, ct)).StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
        (await client.PostAsJsonAsync("/api/v1/students", new { firstName = StudentFirstName, email = "bozuk-eposta" }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(new Uri($"/api/v1/students?search={StudentLastName}", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Enrollment, a fee, a payment and its cancellation with a free-text reason.
        var sport = await IdOfAsync(await client.PostAsJsonAsync("/api/v1/sports", new { name = "Yüzme" }, ct));
        var facility = await IdOfAsync(await client.PostAsJsonAsync("/api/v1/facilities", new
        {
            name = "Kapalı Havuz",
            type = "Pool",
            address = new { line = "Deniz Cad. No:5", district = "Kadıköy", city = "İstanbul" },
            capacity = 40,
        }, ct));
        var group = await IdOfAsync(await client.PostAsJsonAsync("/api/v1/groups", new
        {
            sportId = sport,
            name = "Minikler",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity = 10,
            facilityId = facility,
        }, ct));
        (await client.PostAsJsonAsync($"/api/v1/groups/{group}/fees", new { effectiveFrom = "2027-01", amount = new { amount = "1250.00", currency = "TRY" } }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var enrollment = await IdOfAsync(await client.PostAsJsonAsync("/api/v1/enrollments", new { studentId, groupId = group, startDate = "2027-01-04" }, ct));
        var payment = await IdOfAsync(await client.PostAsJsonAsync("/api/v1/payments", new
        {
            enrollmentId = enrollment,
            period = "2027-01",
            amount = new { amount = "750.00", currency = "TRY" },
            method = "Cash",
            paidOn = "2027-01-10",
        }, ct));
        (await client.PostAsJsonAsync($"/api/v1/payments/{payment}/cancel", new { reason = CancelReason }, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri($"/api/v1/dues/outstanding?groupId={group}", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

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
        events.ShouldContain(e => e.Contains("RecordPaymentCommand", StringComparison.Ordinal));

        string[] forbidden =
        [
            Email, "gizli.kullanici",
            StudentEmail, GuardianEmail,
            StudentFirstName, StudentLastName, GuardianFirstName, "Acilkisi", "Gizlisoyad",
            BirthDate, "23.07.2015",
            "9876543", "987 65 43", "8765432", "7654321",
            OwnPassword, "Yanlis-Sifre-987", "Gizli-Yeni-Sifre-55",
            CancelReason, "Saklisoyadli ailesi",
            session.AccessToken, session.RefreshToken, newSession.AccessToken, newSession.RefreshToken,
        ];
        ShouldNotContainAny(events, forbidden);
        // ASP.NET Core request logs ("Request starting ... ?search=...") stay off even though the config asks for them.
        events.ShouldNotContain(e => e.Contains("Request starting", StringComparison.Ordinal));
    }

    /// <summary>
    /// The 500 path: a request full of personal data fails with a real database exception (a unique-key violation,
    /// whose PostgreSQL detail would name the duplicate e-mail). Neither the logged exception nor the problem
    /// response may carry the request's body, query, headers, token, or the duplicate value.
    /// </summary>
    [Fact]
    public async Task Unhandled_exceptions_log_no_personal_data()
    {
        var school = await TestSchools.CreateAsync(Api, "gizlilik-500");
        await SeedUserAsync(Email, UserRole.SchoolAdmin, school);
        var sink = new CollectingSink();
        using var api = CollectingApi(sink, services => services.AddSingleton<IStartupFilter>(new DuplicateUserEndpoint(school)));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var ct = TestContext.Current.CancellationToken;
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = OwnPassword }, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(login);

        var query = $"phone={Uri.EscapeDataString(StudentPhone)}&lastName={StudentLastName}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/test/duplicate-user?{query}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { firstName = GuardianFirstName, lastName = StudentLastName, email = Email, phone = GuardianPhone, birthDate = BirthDate }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("Cookie", $"sa_refresh={session.RefreshToken}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(ct);
        using (var problem = JsonDocument.Parse(body))
        {
            problem.RootElement.GetProperty("code").GetString().ShouldBe("Unexpected.Error");
        }

        var events = sink.Rendered();
        // The failure must really be logged, and be the database one, otherwise the test proves nothing.
        events.ShouldContain(e => e.StartsWith("Error Unhandled exception", StringComparison.Ordinal)
            && e.Contains("DbUpdateException", StringComparison.Ordinal)
            && e.Contains("23505", StringComparison.Ordinal));

        string[] forbidden =
        [
            Email, "gizli.kullanici",
            StudentLastName, GuardianFirstName,
            BirthDate,
            "9876543", "987 65 43", "7654321", "765 43 21",
            OwnPassword,
            session.AccessToken, session.RefreshToken,
        ];
        ShouldNotContainAny(events, forbidden);
        foreach (var value in forbidden)
        {
            body.ShouldNotContain(value, Case.Insensitive, $"'{value}' leaked into the problem response.");
        }

        body.ShouldNotContain("duplicate key", Case.Insensitive);
        body.ShouldNotContain("ix_users", Case.Insensitive);
    }

    /// <summary>
    /// The API with every configurable log level at its most verbose, including the categories the API pins in code
    /// (ASP.NET Core request logging, EF Core commands), and a sink that collects every event.
    /// </summary>
    private WebApplicationFactory<Program> CollectingApi(CollectingSink sink, Action<IServiceCollection>? services = null) =>
        Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Verbose",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Verbose",
            }));
            builder.ConfigureTestServices(collection =>
            {
                collection.AddSingleton<ILogEventSink>(sink);
                services?.Invoke(collection);
            });
        });

    private static void ShouldNotContainAny(IReadOnlyList<string> events, string[] forbidden)
    {
        foreach (var value in forbidden)
        {
            events.Where(e => e.Contains(value, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty($"'{value}' leaked into the logs.");
        }
    }

    /// <summary>
    /// A throwaway endpoint behind the real pipeline (authentication, error handling): it reads a body with personal
    /// data and inserts a user whose e-mail already exists, so <c>SaveChanges</c> fails with a unique-key violation.
    /// </summary>
    private sealed class DuplicateUserEndpoint(SchoolId school) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/test/duplicate-user", branch => branch.Run(async context =>
            {
                var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
                var services = context.RequestServices;
                var user = User.Create(
                    school,
                    SporAkademi.Domain.SharedKernel.Email.Create(body.GetProperty("email").GetString()!).Value,
                    services.GetRequiredService<IPasswordHasher>().Hash(OwnPassword),
                    UserRole.Coach,
                    body.GetProperty("firstName").GetString()!,
                    body.GetProperty("lastName").GetString()!,
                    PhoneNumber.Create(body.GetProperty("phone").GetString()!).Value).Value;
                var db = services.GetRequiredService<AppDbContext>();
                db.Add(user);
                await db.SaveChangesAsync(context.RequestAborted);
            }));
        };
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
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

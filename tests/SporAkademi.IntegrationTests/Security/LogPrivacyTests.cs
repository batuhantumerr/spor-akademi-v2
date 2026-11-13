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
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// NFR-PRV-03: log events carry ids and codes only. Representative requests run with every log level on and a
/// collecting sink; no e-mail, phone, name, birth date, password or token value may appear in any event, including the
/// P2-F4 e-mail deliveries (welcome, password reset, fee reminder) and their set-password links.
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
    private const string CoachEmail = "antrenor.gizli@example.com";
    private const string CoachFirstName = "Gizliantrenor";
    private const string ResetPassword = "Sifirlanan-Gizli-77";

    /// <summary>Free text an admin might write; it can name people, so it must not reach the logs either.</summary>
    private const string CancelReason = "Saklisoyadli ailesi yanlış aya ödedi";
    private const string RefundReason = "Velisakli hanım kaydı erken bitirdi";

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

        // A health report whose file name names the student, a refused upload, and a download link (P2-F6).
        using (var report = new MultipartFormDataContent { { new StringContent("HealthReport"), "type" }, { new ByteArrayContent(Testing.TestFiles.Pdf()), "file", $"{StudentLastName} saglik.pdf" } })
        {
            var uploaded = await client.PostAsync(new Uri($"/api/v1/students/{studentId}/documents", UriKind.Relative), report, ct);
            uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);
            (await client.GetAsync(new Uri($"/api/v1/documents/{await IdOfAsync(uploaded)}/link", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var refused = new MultipartFormDataContent { { new StringContent("Photo"), "type" }, { new ByteArrayContent(Testing.TestFiles.Svg()), "file", $"{StudentFirstName}.png" } })
        {
            (await client.PostAsync(new Uri($"/api/v1/students/{studentId}/documents", UriKind.Relative), refused, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Password change (old and new passwords in the body) and a wrong current password.
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Yanlis-Sifre-987", newPassword = "Gizli-Yeni-Sifre-55" }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var changed = await client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = OwnPassword, newPassword = "Gizli-Yeni-Sifre-55" }, ct);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newSession = await SessionFromAsync(changed);

        // P2-F4: e-mails with personal data and set-password links go through the outbox (welcome, reset, fee reminder).
        (await client.PostAsJsonAsync("/api/v1/coaches", new { firstName = CoachFirstName, lastName = StudentLastName, email = CoachEmail, phone = (string?)null }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        using var anonymous = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = Email }, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = "olmayan.gizli@example.com" }, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<DuesReminderJob>().RunAsync(ct)).ShouldBe(1);
        }

        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(ct) > 0)
        {
        }

        Api.Emails.To(CoachEmail).ShouldHaveSingleItem();
        Api.Emails.To(GuardianEmail).ShouldHaveSingleItem().TextBody.ShouldContain(StudentFirstName);
        var resetToken = CapturingEmailSender.TokenIn(Api.Emails.To(Email).Single(m => m.Template == "identity.password-reset"));
        var welcomeToken = CapturingEmailSender.TokenIn(Api.Emails.To(CoachEmail).Single());

        // P2-F1: after the fee reminder was sent (it needs the unpaid due), the due is paid online through the sandbox
        // provider (webhook and browser return); the admin session is still valid until the password reset is confirmed.
        using (var start = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/online-payments", UriKind.Relative)))
        {
            start.Content = JsonContent.Create(new { enrollmentId = enrollment, period = "2027-01" });
            start.Headers.Add("Idempotency-Key", "gizlilik-anahtari-1");
            var started = await client.SendAsync(start, ct);
            started.StatusCode.ShouldBe(HttpStatusCode.Created);
            var attempt = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
            var checkoutUrl = attempt.GetProperty("checkoutUrl").GetString()!;
            var token = checkoutUrl[(checkoutUrl.IndexOf('=', StringComparison.Ordinal) + 1)..];
            (await client.PostAsJsonAsync("/api/v1/sandbox-checkout/complete", new { token, succeed = true }, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            (await client.PostAsync(new Uri("/api/v1/payment-callbacks", UriKind.Relative), form, ct)).StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);

            // P3-13: the online payment is refunded with a reason naming the family; only ids and codes reach the log,
            // and the refund e-mail (with names) goes out through the outbox.
            var paid = await client.GetFromJsonAsync<JsonElement>($"/api/v1/online-payments/{attempt.GetProperty("id").GetGuid()}", ct);
            using var refund = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/payments/{paid.GetProperty("paymentId").GetGuid()}/refund", UriKind.Relative));
            refund.Content = JsonContent.Create(new { reason = RefundReason });
            refund.Headers.Add("Idempotency-Key", "gizlilik-iadesi-1");
            (await client.SendAsync(refund, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        while (await processor.ProcessBatchAsync(ct) > 0)
        {
        }

        (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token = resetToken, newPassword = ResetPassword }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await anonymous.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token = resetToken, newPassword = ResetPassword }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // P2-F5: the student leaves and, past the retention period, the job anonymizes it (counts and ids only).
        using (var after = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }))
        {
            // A minted token: the admin's own sessions were ended by the password reset above.
            after.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", school.Value, Api.Clock.GetUtcNow()));
            (await after.PostAsJsonAsync($"/api/v1/students/{studentId}/leave", new { date = "2027-01-14" }, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Api.Clock.SetUtcNow(new DateTimeOffset(2029, 2, 1, 12, 0, 0, TimeSpan.Zero));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<StudentAnonymizationJob>().RunAsync(ct)).ShouldBe(1);
        }

        var events = sink.Rendered();
        events.ShouldContain(e => e.Contains("Anonymization for school", StringComparison.Ordinal) && e.Contains(school.Value.ToString(), StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("identity.password-reset", StringComparison.Ordinal) && e.Contains("sent", StringComparison.Ordinal));
        // P4-03 (DEC-512): the password-changed notice names the user by id only.
        events.ShouldContain(e => e.Contains("identity.password-changed", StringComparison.Ordinal) && e.Contains("sent", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("Dues reminder", StringComparison.Ordinal) && e.Contains("sent", StringComparison.Ordinal));
        // The test must see real request logs, otherwise it proves nothing.
        events.ShouldContain(e => e.Contains("LoginCommand", StringComparison.Ordinal) && e.Contains("Auth.InvalidCredentials", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("RegisterStudentCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("RecordPaymentCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("StartOnlinePaymentCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("ApplyProviderStateCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("RequestRefundCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("ApplyRefundResultCommand", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("e-mailed to 1 recipients", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("UploadStudentDocumentCommand", StringComparison.Ordinal) && e.Contains("Document.UnsupportedType", StringComparison.Ordinal));

        string[] forbidden =
        [
            Email, "gizli.kullanici",
            StudentEmail, GuardianEmail,
            StudentFirstName, StudentLastName, GuardianFirstName, "Acilkisi", "Gizlisoyad",
            BirthDate, "23.07.2015",
            "9876543", "987 65 43", "8765432", "7654321",
            OwnPassword, "Yanlis-Sifre-987", "Gizli-Yeni-Sifre-55",
            CancelReason, "Saklisoyadli ailesi", RefundReason, "kaydı erken bitirdi",
            session.AccessToken, session.RefreshToken, newSession.AccessToken, newSession.RefreshToken,
            CoachEmail, CoachFirstName, "olmayan.gizli",
            resetToken, welcomeToken, ResetPassword,
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
    /// P3-08 (DEC-399): the hub's WebSocket carries the access token in the query. Even with ASP.NET Core's request logs
    /// and the SignalR categories turned on by configuration, no event may contain the token; the request itself must
    /// be logged (with the value redacted), otherwise the test proves nothing.
    /// </summary>
    [Fact]
    [Trait("Story", "P3-08")]
    public async Task The_hub_access_token_in_the_query_is_never_logged()
    {
        var sink = new CollectingSink();
        using var api = CollectingApi(sink, configuration: new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.SignalR"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Http.Connections"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore.Authentication"] = "Verbose",
        });
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var ct = TestContext.Current.CancellationToken;
        var school = await TestSchools.CreateAsync(Api, "gizlilik-hub");
        var token = TestTokens.Create(Guid.CreateVersion7(), "Parent", school.Value, Api.Clock.GetUtcNow());
        var expired = TestTokens.Create(Guid.CreateVersion7(), "Parent", school.Value, Api.Clock.GetUtcNow().AddHours(-1));

        var negotiated = await client.PostAsync(new Uri($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={token}", UriKind.Relative), null, ct);
        negotiated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var connectionToken = (await negotiated.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("connectionToken").GetString()!;
        // The first long-polling request answers at once; it runs through authentication and the hub with the query token.
        (await client.GetAsync(new Uri($"/hubs/notifications?id={connectionToken}&access_token={token}", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync(new Uri($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={expired}", UriKind.Relative), null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // The token URL-encoded inside another parameter (e.g. a return address) is masked too.
        var encoded = Uri.EscapeDataString($"/hubs/notifications?id={connectionToken}&access_token={token}");
        (await client.GetAsync(new Uri($"/api/v1/me?next={encoded}", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var events = sink.Rendered();
        events.ShouldContain(e => e.Contains("Request starting", StringComparison.Ordinal) && e.Contains("/hubs/notifications", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("id=[REDACTED]", StringComparison.Ordinal));
        events.ShouldContain(e => e.Contains("access_token%3D[REDACTED]", StringComparison.OrdinalIgnoreCase));
        events.ShouldContain(e => e.Contains("access_token=[REDACTED]", StringComparison.Ordinal));
        string[] forbidden = [token, expired, token.Split('.')[1], token.Split('.')[2], expired.Split('.')[2], connectionToken];
        ShouldNotContainAny(events, forbidden);
    }

    [Theory]
    [Trait("Story", "P3-08")]
    [InlineData("/hubs/notifications?id=abc123&access_token=eyJ.x.y", "/hubs/notifications?id=[REDACTED]&access_token=[REDACTED]")]
    [InlineData("GET /hubs/notifications/negotiate?negotiateVersion=1&ACCESS_TOKEN=eyJ.x.y HTTP/1.1", "GET /hubs/notifications/negotiate?negotiateVersion=1&ACCESS_TOKEN=[REDACTED] HTTP/1.1")]
    [InlineData("next=%2Fhubs%3Fid%3Dabc%26access_token%3DeyJ.x.y&page=2", "next=%2Fhubs%3Fid%3D[REDACTED]%26access_token%3D[REDACTED]&page=2")]
    [InlineData("groupId=7&studentid=8", "groupId=7&studentid=8")]
    public void Hub_secrets_are_masked_in_any_text(string text, string expected) =>
        SporAkademi.Api.Observability.AccessTokenRedaction.RedactText(text).ShouldBe(expected);

    /// <summary>
    /// The API with every configurable log level at its most verbose, including the categories the API pins in code
    /// (ASP.NET Core request logging, EF Core commands), and a sink that collects every event.
    /// </summary>
    private WebApplicationFactory<Program> CollectingApi(
        CollectingSink sink,
        Action<IServiceCollection>? services = null,
        IReadOnlyDictionary<string, string?>? configuration = null) =>
        Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Serilog:MinimumLevel:Default"] = "Verbose",
                    ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
                    ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Verbose",
                })
                .AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()));
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
    /// A throwaway, anonymous endpoint appended after the real pipeline, so the global error handler and request logging see it (the bearer token and cookie are sent only to check they never reach the logs): it reads a body with personal
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

using System.Text.Json.Serialization;
using SporAkademi.Api;
using SporAkademi.Api.Authentication;
using SporAkademi.Api.Endpoints.Announcements;
using SporAkademi.Api.Endpoints.Attendance;
using SporAkademi.Api.Endpoints.Billing;
using SporAkademi.Api.Endpoints.Dashboard;
using SporAkademi.Api.Endpoints.Enrollments;
using SporAkademi.Api.Endpoints.Groups;
using SporAkademi.Api.Endpoints.Identity;
using SporAkademi.Api.Endpoints.Organization;
using SporAkademi.Api.Endpoints.Parents;
using SporAkademi.Api.Endpoints.Reports;
using SporAkademi.Api.Endpoints.Students;
using SporAkademi.Api.Endpoints.Tenancy;
using SporAkademi.Api.Errors;
using SporAkademi.Api.Jobs;
using SporAkademi.Api.Observability;
using SporAkademi.Api.OpenApi;
using SporAkademi.Api.Realtime;
using SporAkademi.Api.Security;
using SporAkademi.Application;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

StartupChecks.EnsureRuntimeSupportsTurkishLocale();

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// NFR-SEC-12: request bodies are capped at 1 MB. No Server header: it only tells an attacker what runs here (P4-01).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 1_048_576;
    kestrel.AddServerHeader = false;
});

builder.AddStructuredLogging();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>(); // before AddInfrastructure, which only adds a fallback
builder.Services.AddRealtime(); // P3-08; also before AddInfrastructure (its notifier is the fallback's replacement)
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication();
builder.Services.AddTrustedForwardedHeaders(); // P4-02 (DEC-507): client address and scheme from the known proxy only
builder.Services.AddApiRateLimiting(); // P4-02: the general limit, the 429 shape and Retry-After
builder.Services.AddAuthRateLimiting();
builder.Services.AddPaymentNotificationRateLimiting();
builder.Services.AddParentRateLimiting();
builder.Services.AddInvitationRateLimiting();
builder.Services.AddReportExportRateLimiting();
builder.Services.AddJobsDashboard();

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemShape.Complete);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddApiDocument();

builder.Services.AddHsts(hsts =>
{
    hsts.MaxAge = TimeSpan.FromDays(365);
    hsts.IncludeSubDomains = false;
    hsts.Preload = false;
});

var app = builder.Build();
// First: everything after it (HSTS, rate limits, logs) sees the client's address and scheme, not the proxy's.
app.UseTrustedForwardedHeaders();
app.UseMiddleware<SecurityHeadersMiddleware>(); // P4-01 (NFR-SEC-13), on every response
if (app.Environment.IsProduction())
{
    // NFR-SEC-02 (DEC-501): only in Production and only on HTTPS requests (UseHsts skips plain HTTP and localhost).
    // Behind the TLS-terminating proxy the request is HTTPS through X-Forwarded-Proto from a known proxy (DEC-507).
    app.UseHsts();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<NoStoreMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
// After authentication, so a policy can partition by the signed-in user (the parent API, DEC-388); the other policies
// partition by client IP and are unaffected.
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<UserLogEnrichmentMiddleware>();
app.UseAuthorization();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

app.MapHealthEndpoints();
app.MapIdentityEndpoints();
app.MapCoachEndpoints();
app.MapSchoolEndpoints();
app.MapDataRetentionEndpoints();
app.MapBillingRulesEndpoints();
app.MapOrganizationEndpoints();
app.MapStudentEndpoints();
app.MapDocumentEndpoints();
app.MapGroupsEndpoints();
app.MapAnnouncementEndpoints();
app.MapEnrollmentEndpoints();
app.MapAttendanceEndpoints();
app.MapBillingEndpoints();
app.MapOnlinePaymentEndpoints();
app.MapDashboardEndpoints();
app.MapReportEndpoints();
app.MapParentEndpoints();
app.MapRealtime();
app.MapJobsDashboard();
app.MapApiDocument();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;

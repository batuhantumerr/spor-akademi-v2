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
using SporAkademi.Api.Endpoints.Students;
using SporAkademi.Api.Endpoints.Tenancy;
using SporAkademi.Api.Errors;
using SporAkademi.Api.Observability;
using SporAkademi.Api.OpenApi;
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

// NFR-SEC-12: request bodies are capped at 1 MB.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 1_048_576);

builder.AddStructuredLogging();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>(); // before AddInfrastructure, which only adds a fallback
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuthentication();
builder.Services.AddAuthRateLimiting();
builder.Services.AddPaymentNotificationRateLimiting();

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemShape.Complete);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddApiDocument();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<NoStoreMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<UserLogEnrichmentMiddleware>();
app.UseAuthorization();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();

app.MapHealthEndpoints();
app.MapIdentityEndpoints();
app.MapCoachEndpoints();
app.MapSchoolEndpoints();
app.MapOrganizationEndpoints();
app.MapStudentEndpoints();
app.MapGroupsEndpoints();
app.MapAnnouncementEndpoints();
app.MapEnrollmentEndpoints();
app.MapAttendanceEndpoints();
app.MapBillingEndpoints();
app.MapOnlinePaymentEndpoints();
app.MapDashboardEndpoints();
app.MapApiDocument();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;

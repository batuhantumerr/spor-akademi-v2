using System.Text.Json.Serialization;
using SporAkademi.Api;
using SporAkademi.Api.Authentication;
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

builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemShape.Complete);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddApiDocument();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<UserLogEnrichmentMiddleware>();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapApiDocument();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;

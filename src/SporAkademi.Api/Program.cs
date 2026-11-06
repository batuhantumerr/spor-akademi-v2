using SporAkademi.Api;
using SporAkademi.Application;
using SporAkademi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

StartupChecks.EnsureRuntimeSupportsTurkishLocale();

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.AddStructuredLogging();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;

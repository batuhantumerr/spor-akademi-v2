using SporAkademi.Api;
using SporAkademi.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

StartupChecks.EnsureRuntimeSupportsTurkishLocale();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapHealthEndpoints();

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;

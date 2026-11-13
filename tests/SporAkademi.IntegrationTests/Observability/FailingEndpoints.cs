using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Identity;

namespace SporAkademi.IntegrationTests.Observability;

/// <summary>
/// Throwaway anonymous endpoints appended after the real pipeline, so the global error handler, tracing and error
/// tracking see them: <c>/test/duplicate-user?school=…</c> inserts a user whose e-mail (from the body) already exists, so
/// <c>SaveChanges</c> fails with a unique-key violation (a database error span); <c>/test/throw</c> throws an exception
/// whose message quotes the body's e-mail, as careless code might.
/// </summary>
internal sealed class DuplicateUserEndpoint : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.Map("/test/duplicate-user", branch => branch.Run(async context =>
        {
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            var school = new SchoolId(Guid.Parse(context.Request.Query["school"].ToString()));
            var services = context.RequestServices;
            var user = User.Create(
                school,
                Email.Create(body.GetProperty("email").GetString()!).Value,
                services.GetRequiredService<IPasswordHasher>().Hash(IdentityTest.OwnPassword),
                UserRole.Coach,
                "Kopya",
                "Kullanici",
                null).Value;
            var db = services.GetRequiredService<AppDbContext>();
            db.Add(user);
            await db.SaveChangesAsync(context.RequestAborted);
        }));
        app.Map("/test/bad-uuid", branch => branch.Run(async context =>
        {
            // PostgreSQL quotes the rejected value in its message: 22P02 invalid input syntax for type uuid: "…".
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"SELECT CAST({body.GetProperty("value").GetString()} AS uuid)", context.RequestAborted);
        }));
        app.Map("/test/throw", branch => branch.Run(async context =>
        {
            var body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            throw new InvalidOperationException($"Could not process the request of {body.GetProperty("email").GetString()}.");
        }));
    };
}

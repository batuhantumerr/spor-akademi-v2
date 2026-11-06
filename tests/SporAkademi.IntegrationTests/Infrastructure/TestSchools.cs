using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>Two-school fixture data: creates schools directly and hands out role-scoped clients.</summary>
public static class TestSchools
{
    public static async Task<SchoolId> CreateAsync(ApiFactory api, string slug)
    {
        ArgumentNullException.ThrowIfNull(api);
        var school = School.Create($"Okul {slug}", Slug.Create(slug).Value, "Europe/Istanbul", RetentionPolicy.Default).Value;
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>A client acting as a School Admin of <paramref name="school"/> (token minted for the test).</summary>
    public static HttpClient SchoolAdminClient(this ApiFactory api, SchoolId school)
    {
        ArgumentNullException.ThrowIfNull(api);
        var client = api.CreateClient();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", school.Value, api.Clock.GetUtcNow()));
        return client;
    }

    public static HttpClient CoachClient(this ApiFactory api, SchoolId school, Guid? coachId = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        var client = api.CreateClient();
        client.Authorize(TestTokens.Create(coachId ?? Guid.CreateVersion7(), "Coach", school.Value, api.Clock.GetUtcNow()));
        return client;
    }
}

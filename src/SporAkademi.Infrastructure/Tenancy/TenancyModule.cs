using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Abstractions.Caching;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

internal sealed class TenancyModule(AppDbContext dbContext, TimeProvider timeProvider, ITenantCache cache) : ITenancyModule
{
    /// <summary>Not cached (DEC-311): deactivating a school must take effect on the next request.</summary>
    public Task<bool> IsSchoolActiveAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AnyAsync(s => s.Id == schoolId && s.Status == SchoolStatus.Active, cancellationToken);

    public async Task<DateOnly> TodayAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        DateOnly.FromDateTime(await LocalNowAsync(schoolId, cancellationToken));

    public async Task<DateTime> LocalNowAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        School.LocalNowIn(
            await TimeZoneAsync(schoolId, cancellationToken)
                ?? throw new InvalidOperationException("The school of a signed-in user must exist."),
            timeProvider);

    public async Task<RetentionPolicy?> RetentionPolicyAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        (await dbContext.Set<School>().AsNoTracking()
            .Where(s => s.Id == schoolId)
            .Select(s => new { s.RetentionPolicy })
            .SingleOrDefaultAsync(cancellationToken))?.RetentionPolicy;

    /// <summary>Cached per school (DEC-311): read on almost every request that needs "today", and a saved change to the school invalidates it.</summary>
    public async Task<string?> TimeZoneAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            TenantCacheKey.For(schoolId, CacheArea.School, "time-zone"),
            async ct => await dbContext.Set<School>().AsNoTracking()
                .Where(s => s.Id == schoolId)
                .Select(s => (string?)s.TimeZone)
                .SingleOrDefaultAsync(ct),
            cancellationToken);

    /// <summary>Not cached: read once per e-mail, in background work.</summary>
    public Task<string?> SchoolNameAsync(SchoolId schoolId, CancellationToken cancellationToken) =>
        dbContext.Set<School>().AsNoTracking()
            .Where(s => s.Id == schoolId)
            .Select(s => (string?)s.Name)
            .SingleOrDefaultAsync(cancellationToken);
}

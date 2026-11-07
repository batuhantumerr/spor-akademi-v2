using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Jobs;

/// <summary>
/// The only way a job touches school data (DEC-334): the schools are listed explicitly and the work runs once per
/// school, each time in a new DI scope whose <see cref="BackgroundTenantContext"/> is that school. The tenant query
/// filters and the write guard (ADR-0003) then behave as in that school's requests, and nothing tracked for one
/// school can reach the next. A failing school is logged (ids only) and the others still run; the run then fails,
/// so the panel shows it.
/// </summary>
internal sealed partial class TenantJobRunner(IServiceScopeFactory scopeFactory, ILogger<TenantJobRunner> logger)
{
    /// <summary>Runs <paramref name="work"/> for every school (active or not), in id order.</summary>
    /// <returns>The number of schools.</returns>
    /// <exception cref="TenantJobException">One or more schools failed.</exception>
    public async Task<int> ForEachSchoolAsync(
        string jobName,
        Func<IServiceProvider, SchoolId, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var schools = await ListSchoolsAsync(cancellationToken);
        var failed = 0;
        foreach (var school in schools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(school.Value);
                await work(scope.ServiceProvider, school, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One school's failure must not stop the others; the run fails at the end.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failed++;
                LogSchoolFailed(logger, jobName, school.Value, exception.GetType().FullName ?? exception.GetType().Name);
            }
        }

        if (failed > 0)
        {
            throw new TenantJobException($"{jobName} failed for {failed} of {schools.Count} schools.");
        }

        return schools.Count;
    }

    /// <summary>
    /// Runs one more unit of work for <paramref name="school"/> in a fresh scope, from inside that school's
    /// <see cref="ForEachSchoolAsync"/> work: for batches that must not share a DbContext (and its tracked rows).
    /// </summary>
    public async Task<T> ForSchoolAsync<T>(SchoolId school, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(school.Value);
        return await work(scope.ServiceProvider, cancellationToken);
    }

    /// <summary>Runs platform-level work (no school): tenant filters match no rows, so it must name what it touches.</summary>
    public async Task<T> ForPlatformAsync<T>(Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(null);
        return await work(scope.ServiceProvider, cancellationToken);
    }

    private Task<List<SchoolId>> ListSchoolsAsync(CancellationToken cancellationToken) =>
        ForPlatformAsync(
            (services, ct) => services.GetRequiredService<AppDbContext>().Set<School>().AsNoTracking()
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .ToListAsync(ct),
            cancellationToken);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job} failed for school {SchoolId} with {Error}")]
    private static partial void LogSchoolFailed(ILogger logger, string job, Guid schoolId, string error);
}

/// <summary>A per-school job finished, but not for every school.</summary>
public sealed class TenantJobException : Exception
{
    public TenantJobException()
    {
    }

    public TenantJobException(string message)
        : base(message)
    {
    }

    public TenantJobException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

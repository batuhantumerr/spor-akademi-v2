using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>Base class: fresh API per test class, clean database before every test.</summary>
[Collection(DatabaseTestGroup.Name)]
public abstract class IntegrationTest(DatabaseFixture database) : IAsyncLifetime
{
    protected DatabaseFixture Database { get; } = database;

    protected ApiFactory Api { get; private set; } = null!;

    protected HttpClient Client { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync()
    {
        await Database.ResetAsync();
        Api = CreateApi();
        Client = Api.CreateClient();
    }

    /// <summary>The API under test; override to configure it differently (e.g. with a distributed cache).</summary>
    protected virtual ApiFactory CreateApi() => new(Database.AppConnectionString, storage: Database.Storage.Settings);

    public virtual async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Api.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs <paramref name="action"/> with services from a fresh DI scope, like one request.</summary>
    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Api.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>
    /// Inspects rows of every school at once, for assertions only: a context on the migrator's connection with the
    /// maintenance switch (row level security, P4-04, DEC-522). The application never reads across schools this way;
    /// call <c>IgnoreQueryFilters</c> on the sets, since no school is current.
    /// </summary>
    protected async Task<T> AcrossSchoolsAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Database.MigratorConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new AppDbContext(options, new AnonymousCurrentUser());
        return await action(db);
    }

    protected async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = Api.Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }
}

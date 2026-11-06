using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Persistence;

public sealed class UnitOfWorkTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static School NewSchool(string slug = "test-okulu") =>
        School.Create("Test Okulu", Slug.Create(slug).Value, "Europe/Istanbul", RetentionPolicy.Default).Value;

    [Fact]
    public async Task Successful_operation_is_committed_with_audit_columns()
    {
        var school = NewSchool();

        var result = await InScopeAsync(sp => sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            ct =>
            {
                sp.GetRequiredService<AppDbContext>().Add(school);
                return Task.FromResult(Result.Success(school.Id));
            },
            TestContext.Current.CancellationToken));

        result.IsSuccess.ShouldBeTrue();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var stored = await db.Set<School>().AsNoTracking().SingleAsync(s => s.Id == school.Id, TestContext.Current.CancellationToken);
            stored.Slug.Value.ShouldBe("test-okulu");
            stored.RetentionPolicy.ShouldBe(RetentionPolicy.Default);
            db.Entry(stored).Property<DateTimeOffset>("CreatedAt").CurrentValue.ShouldBe(Api.Clock.GetUtcNow());
        });
    }

    [Fact]
    public async Task Failed_result_rolls_back()
    {
        var result = await InScopeAsync(sp => sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            ct =>
            {
                sp.GetRequiredService<AppDbContext>().Add(NewSchool());
                return Task.FromResult(Result.Failure<SchoolId>(SchoolErrors.SlugTaken));
            },
            TestContext.Current.CancellationToken));

        result.Error.ShouldBe(SchoolErrors.SlugTaken);
        (await CountSchoolsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Exception_rolls_back_and_propagates()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => InScopeAsync(sp => sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync<SchoolId>(
            async ct =>
            {
                var db = sp.GetRequiredService<AppDbContext>();
                db.Add(NewSchool());
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            },
            TestContext.Current.CancellationToken)));

        (await CountSchoolsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Stale_update_is_a_concurrency_conflict()
    {
        var school = NewSchool();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(school);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        // Two "requests" read the same row; the second write must fail.
        await using var first = Api.Services.CreateAsyncScope();
        await using var second = Api.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstCopy = await firstDb.Set<School>().SingleAsync(s => s.Id == school.Id, TestContext.Current.CancellationToken);
        var secondCopy = await secondDb.Set<School>().SingleAsync(s => s.Id == school.Id, TestContext.Current.CancellationToken);

        firstDb.Entry(firstCopy).Property(s => s.Name).CurrentValue = "Birinci";
        await firstDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await second.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            ct =>
            {
                secondDb.Entry(secondCopy).Property(s => s.Name).CurrentValue = "İkinci";
                return Task.FromResult(Result.Success(true));
            },
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(PersistenceErrors.ConcurrencyConflict);
    }

    [Fact]
    public async Task Slug_is_unique_in_the_database()
    {
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(NewSchool("ayni-slug"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await Should.ThrowAsync<DbUpdateException>(() => InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(NewSchool("ayni-slug"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }));
    }

    private Task<int> CountSchoolsAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<School>().CountAsync(TestContext.Current.CancellationToken));
}

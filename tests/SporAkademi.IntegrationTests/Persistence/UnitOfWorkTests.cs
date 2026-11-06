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
            (await AuditOfAsync(db, school.Id)).CreatedAt.ShouldBe(Api.Clock.GetUtcNow());
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

    [Fact]
    public async Task Xmin_is_a_system_column_not_a_table_column()
    {
        var columns = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"select column_name as \"Value\" from information_schema.columns where table_schema = 'tenancy' and table_name = 'schools'")
            .ToListAsync(TestContext.Current.CancellationToken));

        columns.ShouldNotContain("xmin");
        columns.ShouldContain("status");
    }

    [Fact]
    public async Task Enums_are_stored_as_text_and_updates_restamp_only_updated_columns()
    {
        var school = NewSchool();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(school);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        var created = Api.Clock.GetUtcNow();
        Api.Clock.Advance(TimeSpan.FromHours(1));

        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var stored = await db.Set<School>().SingleAsync(s => s.Id == school.Id, TestContext.Current.CancellationToken);
            db.Entry(stored).Property(s => s.Name).CurrentValue = "Yeni Ad";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var status = await db.Database
                .SqlQuery<string>($"select status as \"Value\" from tenancy.schools where id = {school.Id.Value}")
                .SingleAsync(TestContext.Current.CancellationToken);
            status.ShouldBe("Active");

            var audit = await AuditOfAsync(db, school.Id);
            audit.CreatedAt.ShouldBe(created);
            audit.UpdatedAt.ShouldBe(Api.Clock.GetUtcNow());
            audit.CreatedBy.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Nested_unit_of_work_joins_the_outer_transaction()
    {
        var result = await InScopeAsync(sp =>
        {
            var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
            return unitOfWork.ExecuteInTransactionAsync(
                ct => unitOfWork.ExecuteInTransactionAsync(
                    _ =>
                    {
                        sp.GetRequiredService<AppDbContext>().Add(NewSchool());
                        return Task.FromResult(Result.Success(1));
                    },
                    ct),
                TestContext.Current.CancellationToken);
        });

        result.IsSuccess.ShouldBeTrue();
        (await CountSchoolsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Waiting_on_a_locked_row_times_out_as_a_lock_timeout_error()
    {
        var school = NewSchool();
        await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(school);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        // Another connection holds the row lock for longer than lock_timeout.
        await using var holder = new Npgsql.NpgsqlConnection(Database.AppConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var holderTransaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var command = new Npgsql.NpgsqlCommand("select 1 from tenancy.schools where id = @id for update", holder, holderTransaction))
        {
            command.Parameters.AddWithValue("id", school.Id.Value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var result = await InScopeAsync(sp => sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            async ct =>
            {
                await sp.GetRequiredService<AppDbContext>().Database
                    .ExecuteSqlAsync($"select 1 from tenancy.schools where id = {school.Id.Value} for update", ct);
                return Result.Success(true);
            },
            TestContext.Current.CancellationToken));

        result.Error.ShouldBe(PersistenceErrors.LockTimeout);
    }

    // Shadow properties are not tracked for no-tracking queries, so project them explicitly.
    private static Task<AuditColumns> AuditOfAsync(AppDbContext db, SchoolId id) =>
        db.Set<School>().Where(s => s.Id == id)
            .Select(s => new AuditColumns(
                EF.Property<DateTimeOffset>(s, "CreatedAt"),
                EF.Property<Guid?>(s, "CreatedBy"),
                EF.Property<DateTimeOffset>(s, "UpdatedAt")))
            .SingleAsync(TestContext.Current.CancellationToken);

    private sealed record AuditColumns(DateTimeOffset CreatedAt, Guid? CreatedBy, DateTimeOffset UpdatedAt);

    private Task<int> CountSchoolsAsync() =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<School>().CountAsync(TestContext.Current.CancellationToken));
}

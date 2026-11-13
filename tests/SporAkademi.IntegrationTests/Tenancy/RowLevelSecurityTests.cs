using System.Data.Common;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

/// <summary>
/// P4-04 (DEC-520 – DEC-525): PostgreSQL row level security as the database's own tenant isolation layer. The query
/// filters are bypassed on purpose here (<c>IgnoreQueryFilters</c>, raw SQL), so only the policies stand between a
/// school and another school's rows.
/// </summary>
[Trait("Story", "P4-04")]
public sealed class RowLevelSecurityTests(DatabaseFixture database) : IdentityTest(database)
{
    /// <summary>
    /// Tables without a <c>tenant_id</c> column on purpose (DEC-527): each belongs to a row of its parent, and its policy
    /// shows a row only while the parent row is visible (the parent's own policy decides).
    /// </summary>
    private static readonly Dictionary<string, string> OwnedTables = new(StringComparer.Ordinal)
    {
        ["groups.group_fees"] = "groups.groups",
        ["identity.password_tokens"] = "identity.users",
        ["identity.refresh_tokens"] = "identity.users",
    };

    /// <summary>Platform tables (DEC-105, DEC-302): a school sees its own rows; work without a school sees them all.</summary>
    private static readonly string[] PlatformTables = ["audit.audit_entries", "identity.users", "outbox.messages", "tenancy.schools"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_table_forces_row_level_security_with_a_policy_that_reads_the_school()
    {
        var tables = await RowsAsync(
            Database.MigratorConnectionString,
            """
            SELECT n.nspname || '.' || c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped),
                   (SELECT p.qual || ' | ' || p.with_check FROM pg_policies p
                    WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'tenant_isolation'
                      AND p.cmd = 'ALL' AND p.permissive = 'PERMISSIVE' AND p.roles = '{public}'),
                   (SELECT p.qual || ' | ' || p.with_check FROM pg_policies p
                    WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'maintenance'
                      AND p.cmd = 'ALL' AND p.roles = ARRAY[pg_get_userbyid(c.relowner)]::name[])
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('public', 'hangfire', 'information_schema')
              AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY 1
            """);

        tables.Count.ShouldBeGreaterThan(20);
        foreach (var row in tables)
        {
            var (name, enabled, forced, hasTenant) = ((string)row[0]!, (bool)row[1]!, (bool)row[2]!, (bool)row[3]!);
            var (policy, maintenance) = ((string?)row[4], (string?)row[5]);

            // A new table without its policy fails here: call RowLevelSecurity.ForTenantTable in its migration.
            enabled.ShouldBeTrue($"{name} must enable row level security");
            forced.ShouldBeTrue($"{name} must force row level security (the owner too)");
            policy.ShouldNotBeNull($"{name} must have the tenant_isolation policy");
            maintenance.ShouldNotBeNull($"{name} must have the owner's maintenance policy");
            maintenance.ShouldContain("is_maintenance()");

            // The condition must read the school: USING (true) would pass every other check.
            if (OwnedTables.TryGetValue(name, out var parent))
            {
                policy.ShouldContain($"FROM {parent}", Case.Sensitive, $"{name} must follow its parent {parent}");
            }
            else
            {
                (hasTenant || PlatformTables.Contains(name)).ShouldBeTrue($"{name} has no tenant_id: scope it to a school, or list it with its parent");
                policy.Split(" | ").ShouldAllBe(condition => condition.Contains("current_tenant_id()"));
                if (PlatformTables.Contains(name))
                {
                    policy.Split(" | ").ShouldAllBe(condition => condition.Contains("is_platform_scope()"));
                }
                else
                {
                    policy.ShouldNotContain("is_platform_scope()");
                }
            }
        }

        // The lists stay current: every listed table still exists.
        var names = tables.Select(r => (string)r[0]!).ToList();
        foreach (var listed in OwnedTables.Keys.Concat(PlatformTables))
        {
            names.ShouldContain(listed);
        }
    }

    [Fact]
    public async Task Owned_rows_follow_their_parent()
    {
        var schoolA = await SeedSchoolAsync("okul-a");
        var schoolB = await SeedSchoolAsync("okul-b");
        await SeedUserAsync("coach-a@example.com", UserRole.Coach, schoolA);
        await SeedUserAsync("coach-b@example.com", UserRole.Coach, schoolB);
        await LoginOkAsync("coach-a@example.com");
        await LoginOkAsync("coach-b@example.com");

        var asA = await AsSchoolAsync(schoolA, db => db.Database
            .SqlQuery<int>($"SELECT count(DISTINCT user_id)::int AS \"Value\" FROM identity.refresh_tokens").SingleAsync(Ct));
        var platform = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Database
            .SqlQuery<int>($"SELECT count(DISTINCT user_id)::int AS \"Value\" FROM identity.refresh_tokens").SingleAsync(Ct));

        asA.ShouldBe(1);
        platform.ShouldBe(2);
    }

    [Fact]
    public async Task The_application_role_cannot_bypass_row_level_security()
    {
        var rows = await RowsAsync(
            Database.AppConnectionString,
            "SELECT rolbypassrls, rolsuper FROM pg_roles WHERE rolname = current_user");

        rows.Single().ShouldBe([false, false]);
    }

    [Fact]
    public async Task Without_query_filters_another_schools_rows_cannot_be_read()
    {
        var (schoolA, schoolB) = await TwoSchoolsWithSportsAsync();

        var (ef, raw) = await AsSchoolAsync(schoolA, async db =>
        {
            var names = await db.Set<Sport>().IgnoreQueryFilters().Select(s => s.Name).ToListAsync(Ct);
            var count = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM organization.sports").SingleAsync(Ct);
            return (names, count);
        });

        ef.ShouldBe(["Futbol"]);
        raw.ShouldBe(1);
        (await AsSchoolAsync(schoolB, db => db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM organization.sports WHERE tenant_id = {schoolA.Value}")
            .SingleAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Another_schools_row_cannot_be_inserted()
    {
        var (schoolA, schoolB) = await TwoSchoolsWithSportsAsync();

        var error = await Should.ThrowAsync<PostgresException>(() => AsSchoolAsync(schoolA, db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO organization.sports
            SELECT (jsonb_populate_record(NULL::organization.sports,
                    to_jsonb(s) || jsonb_build_object('id', gen_random_uuid(), 'tenant_id', {schoolB.Value}, 'name', 'Sızma', 'name_key', 'sızma'))).*
            FROM organization.sports s
            """,
            Ct)));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await AsSchoolAsync(schoolB, db => db.Set<Sport>().Select(s => s.Name).ToListAsync(Ct))).ShouldBe(["Yüzme"]);
    }

    [Fact]
    public async Task Another_schools_rows_cannot_be_changed_or_deleted()
    {
        var (schoolA, schoolB) = await TwoSchoolsWithSportsAsync();

        var (updated, deleted) = await AsSchoolAsync(schoolA, async db => (
            await db.Database.ExecuteSqlAsync($"UPDATE organization.sports SET name = 'Hack', name_key = 'hack' WHERE tenant_id = {schoolB.Value}", Ct),
            await db.Database.ExecuteSqlAsync($"DELETE FROM organization.sports WHERE tenant_id = {schoolB.Value}", Ct)));

        updated.ShouldBe(0);
        deleted.ShouldBe(0);
        (await AsSchoolAsync(schoolB, db => db.Set<Sport>().Select(s => s.Name).ToListAsync(Ct))).ShouldBe(["Yüzme"]);
    }

    [Fact]
    public async Task A_row_cannot_be_moved_to_another_school()
    {
        var (schoolA, schoolB) = await TwoSchoolsWithSportsAsync();

        var error = await Should.ThrowAsync<PostgresException>(() => AsSchoolAsync(schoolA, db => db.Database.ExecuteSqlAsync(
            $"UPDATE organization.sports SET tenant_id = {schoolB.Value}",
            Ct)));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Without_a_school_tenant_tables_show_no_rows_even_without_filters()
    {
        await TwoSchoolsWithSportsAsync();

        // Anonymous scope (login, webhook, platform work): the platform flag does not open school tables.
        var count = await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<Sport>().IgnoreQueryFilters().CountAsync(Ct));

        count.ShouldBe(0);
    }

    [Fact]
    public async Task A_connection_without_the_session_settings_sees_nothing()
    {
        var (schoolA, _) = await TwoSchoolsWithSportsAsync();
        await SeedUserAsync("coach@example.com", UserRole.Coach, schoolA);

        // E.g. a future raw Npgsql connection that skips the EF interceptor: fail closed, platform tables included.
        var rows = await RowsAsync(
            Database.AppConnectionString,
            """
            SELECT (SELECT count(*) FROM organization.sports), (SELECT count(*) FROM identity.users),
                   (SELECT count(*) FROM tenancy.schools), (SELECT count(*) FROM outbox.messages)
            """);

        rows.Single().ShouldBe([0L, 0L, 0L, 0L]);
    }

    [Fact]
    public async Task The_maintenance_switch_only_works_for_the_migrator()
    {
        await TwoSchoolsWithSportsAsync();

        var app = await RowsAsync(
            Database.AppConnectionString,
            "SELECT set_config('app.maintenance', 'on', false), (SELECT count(*) FROM organization.sports)");
        var migrator = await RowsAsync(
            Database.MigratorConnectionString,
            "SELECT current_setting('app.maintenance', true), (SELECT count(*) FROM organization.sports)");

        app.Single()[1].ShouldBe(0L);
        migrator.Single().ShouldBe(["on", 2L]);
    }

    [Fact]
    public async Task A_school_sees_only_its_own_users_and_school_but_login_finds_users_of_every_school()
    {
        var schoolA = await SeedSchoolAsync("okul-a");
        var schoolB = await SeedSchoolAsync("okul-b");
        await SeedUserAsync("coach-a@example.com", UserRole.Coach, schoolA);
        await SeedUserAsync("coach-b@example.com", UserRole.Coach, schoolB);

        var (users, schools) = await AsSchoolAsync(schoolA, async db => (
            await db.Set<User>().Select(u => u.Email.Value).ToListAsync(Ct),
            await db.Set<School>().Select(s => s.Id).ToListAsync(Ct)));

        users.ShouldBe(["coach-a@example.com"]);
        schools.ShouldBe([schoolA]);

        // DEC-105: login is anonymous and searches every school by e-mail (the platform flag).
        await LoginOkAsync("coach-a@example.com");
        await LoginOkAsync("coach-b@example.com");
        (await InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<User>().CountAsync(Ct))).ShouldBe(2);
    }

    [Fact]
    public async Task A_data_migration_run_by_the_migrator_sees_every_school()
    {
        await TwoSchoolsWithSportsAsync();
        const string check = "DO $$ BEGIN IF (SELECT count(*) FROM organization.sports) <> 2 THEN RAISE EXCEPTION 'row level security hid rows'; END IF; END $$;";

        // The SQL every migration is generated into (bundle, ef.sh, script): it turns the owner's maintenance switch on.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Database.PlainMigratorConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new AppDbContext(options, new AnonymousCurrentUser());
        var sql = string.Join('\n', db.GetService<IMigrationsSqlGenerator>().Generate([new SqlOperation { Sql = check }]).Select(c => c.CommandText));

        sql.ShouldStartWith("SET app.maintenance = 'on';");
        await RowsAsync(Database.PlainMigratorConnectionString, sql);
        // Without it the migrator, bound by FORCE ROW LEVEL SECURITY, sees no school's rows.
        (await RowsAsync(Database.PlainMigratorConnectionString, "SELECT count(*) FROM organization.sports")).Single()[0].ShouldBe(0L);
    }

    [Fact]
    public async Task Anonymous_endpoints_ignore_a_bearer_token_of_another_school()
    {
        var schoolA = await SeedSchoolAsync("okul-a");
        var schoolB = await SeedSchoolAsync("okul-b");
        await SeedUserAsync("coach-b@example.com", UserRole.Coach, schoolB);
        var session = await LoginOkAsync("coach-b@example.com");

        // A stale or foreign access token on an anonymous call must not narrow it to that school (DEC-526).
        Http.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", schoolA.Value, Api.Clock.GetUtcNow()));

        (await LoginAsync("coach-b@example.com", OwnPassword)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_school_user_cannot_be_written_into_another_school()
    {
        var schoolA = await SeedSchoolAsync("okul-a");
        var schoolB = await SeedSchoolAsync("okul-b");
        await SeedUserAsync("coach-a@example.com", UserRole.Coach, schoolA);
        var coachB = await SeedUserAsync("coach-b@example.com", UserRole.Coach, schoolB);

        var updated = await AsSchoolAsync(schoolA, db => db.Database.ExecuteSqlAsync(
            $"UPDATE identity.users SET first_name = 'Hack' WHERE id = {coachB.Id.Value}",
            Ct));
        var moved = await Should.ThrowAsync<PostgresException>(() => AsSchoolAsync(schoolA, db => db.Database.ExecuteSqlAsync(
            $"UPDATE identity.users SET tenant_id = {schoolB.Value} WHERE tenant_id = {schoolA.Value}",
            Ct)));

        updated.ShouldBe(0);
        moved.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        (await LoadUserAsync(coachB.Id)).FirstName.ShouldBe("Deniz");
    }

    [Fact]
    public async Task The_checkout_lookup_reads_only_the_school_and_id_of_one_token()
    {
        var resolver = await InScopeAsync(sp => sp.GetRequiredService<ICheckoutTenantResolver>().FindAsync("fake", "no-such-token", Ct));

        resolver.ShouldBeNull();
        // The function is the only way across schools; the application role cannot read the table without a school.
        var grants = await RowsAsync(
            Database.MigratorConnectionString,
            """
            SELECT n.nspname || '.' || p.proname || ' app=' || has_function_privilege('spor_akademi_app', p.oid, 'EXECUTE')
                   || ' public=' || EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1
            """);
        grants.Select(r => (string)r[0]!).ShouldBe(
        [
            "audit.purge_expired app=true public=false",
            "billing.count_schools_with_open_period app=true public=false",
            "billing.find_checkout_target app=true public=false",
        ]);
    }

    [Fact]
    public async Task A_pooled_connection_does_not_keep_the_previous_school()
    {
        var (schoolA, _) = await TwoSchoolsWithSportsAsync();
        // One physical connection, and Npgsql's reset on return (DISCARD ALL) switched off: whatever the session holds
        // is still there for the next scope, so only the interceptor can clear the previous school.
        var pooled = new NpgsqlConnectionStringBuilder(Database.AppConnectionString)
        {
            MaxPoolSize = 1,
            NoResetOnClose = true,
            ApplicationName = "rls-pool-test",
        }.ConnectionString;
        await using var api = new ApiFactory(pooled, storage: Database.Storage.Settings);

        int firstPid;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(schoolA.Value);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            firstPid = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct);
            (await db.Set<Sport>().CountAsync(Ct)).ShouldBe(1);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('app.leftover', 'yes', false)", Ct);
        }

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct)).ShouldBe(firstPid);
            (await db.Database.SqlQuery<string>($"SELECT current_setting('app.leftover', true) AS \"Value\"").SingleAsync(Ct)).ShouldBe("yes");
            (await db.Set<Sport>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);
            (await db.Database.SqlQuery<string>($"SELECT coalesce(current_setting('app.tenant_id', true), '') AS \"Value\"").SingleAsync(Ct)).ShouldBe(string.Empty);
        }
    }

    [Fact]
    public async Task The_tenant_policy_can_use_the_tenant_index()
    {
        var (schoolA, _) = await TwoSchoolsWithSportsAsync();

        var plan = await AsSchoolAsync(schoolA, async db =>
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(Ct);
            try
            {
                await using var off = connection.CreateCommand();
                off.CommandText = "SET enable_seqscan = off";
                await off.ExecuteNonQueryAsync(Ct);
                await using var explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN SELECT * FROM organization.sports";
                return string.Join('\n', await ReadAllAsync(explain));
            }
            finally
            {
                await using var reset = connection.CreateCommand();
                reset.CommandText = "RESET enable_seqscan";
                await reset.ExecuteNonQueryAsync(Ct);
                await db.Database.CloseConnectionAsync();
            }
        });

        // current_setting(...)::uuid is stable, so the planner uses it as an index key, not a per-row filter.
        plan.ShouldContain("Index");
        plan.ShouldContain("Index Cond: (tenant_id =");
    }

    private async Task<(SchoolId A, SchoolId B)> TwoSchoolsWithSportsAsync()
    {
        var (schoolA, schoolB) = (await TestSchools.CreateAsync(Api, "okul-a"), await TestSchools.CreateAsync(Api, "okul-b"));
        using var adminA = Api.SchoolAdminClient(schoolA);
        using var adminB = Api.SchoolAdminClient(schoolB);
        (await adminA.PostAsJsonAsync("/api/v1/sports", new { name = "Futbol" }, Ct)).EnsureSuccessStatusCode();
        (await adminB.PostAsJsonAsync("/api/v1/sports", new { name = "Yüzme" }, Ct)).EnsureSuccessStatusCode();
        return (schoolA, schoolB);
    }

    /// <summary>A fresh scope acting for <paramref name="school"/>, like that school's background work.</summary>
    private Task<T> AsSchoolAsync<T>(SchoolId school, Func<AppDbContext, Task<T>> work) =>
        InScopeAsync(sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(school.Value);
            return work(sp.GetRequiredService<AppDbContext>());
        });

    private static async Task<List<object?[]>> RowsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<string>> ReadAllAsync(DbCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }
}

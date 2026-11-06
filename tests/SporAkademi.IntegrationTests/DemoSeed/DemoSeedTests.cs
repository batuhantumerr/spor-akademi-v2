using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing;
using SporAkademi.Application.Groups;
using SporAkademi.DemoSeed;
using SporAkademi.Domain.Groups;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.DemoSeed;

/// <summary>P1-38: the demo seeder creates two realistic schools through the application layer, and every invariant holds.</summary>
public sealed class DemoSeedTests(DatabaseFixture database) : IntegrationTest(database)
{
    private const string Password = "Demo-Sifre-2027";

    /// <summary>The API test clock's moment, so the API sees the same "today" as the seeded history.</summary>
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost SeedHost(string environment = "Development") =>
        DemoSeedHost.Create([], new Dictionary<string, string?> { ["ConnectionStrings:App"] = Database.AppConnectionString }, environment);

    private async Task<long> CountAsync(string sql, Guid? tenant = null)
    {
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenant is { } id)
        {
            command.Parameters.AddWithValue("tenant", id);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private static async Task<T> AskAsAdminAsync<TQuery, T>(IHost host, Guid tenant, TQuery query)
        where TQuery : IQuery<T>
    {
        host.Services.GetRequiredService<SeedActor>().Act(Role.SchoolAdmin, Guid.CreateVersion7(), tenant);
        await using var scope = host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, T>>().Handle(query, Ct);
        return result.Value;
    }

    [Fact]
    public async Task Seeds_two_complete_schools_once_and_every_rule_holds()
    {
        using var host = SeedHost();
        await using var scope = host.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();

        var result = await seeder.RunAsync(new DemoSeedOptions(Now, Password: Password), Ct);

        result.Created.ShouldBeTrue();
        result.Schools.Select(s => s.Slug).ShouldBe(["kadikoy-spor", "bornova-genclik"]);
        foreach (var school in result.Schools)
        {
            var t = school.SchoolId;
            (await CountAsync("SELECT count(*) FROM organization.sports WHERE tenant_id = @tenant", t)).ShouldBeInRange(3, 4);
            (await CountAsync("SELECT count(*) FROM organization.facilities WHERE tenant_id = @tenant", t)).ShouldBe(3);
            (await CountAsync("SELECT count(*) FROM identity.users WHERE tenant_id = @tenant AND role = 'Coach'", t)).ShouldBeInRange(4, 6);
            (await CountAsync("SELECT count(*) FROM groups.groups WHERE tenant_id = @tenant", t)).ShouldBeInRange(8, 12);
            (await CountAsync("SELECT count(*) FROM groups.group_fees f JOIN groups.groups g ON g.id = f.group_id WHERE g.tenant_id = @tenant", t)).ShouldBeGreaterThanOrEqualTo(8);
            (await CountAsync("SELECT count(*) FROM students.students WHERE tenant_id = @tenant", t)).ShouldBeInRange(100, 200);
            (await CountAsync("SELECT count(*) FROM students.students WHERE tenant_id = @tenant AND status = 'Left'", t)).ShouldBe(2);
            (await CountAsync("SELECT count(*) FROM students.students WHERE tenant_id = @tenant AND birth_date <= DATE '2009-01-14'", t)).ShouldBeGreaterThan(0);
            // Siblings: some guardians are linked to more than one student.
            (await CountAsync("SELECT count(*) FROM (SELECT guardian_id FROM students.student_guardians WHERE tenant_id = @tenant GROUP BY guardian_id HAVING count(*) > 1) s", t)).ShouldBeGreaterThan(0);

            // Capacity: never over, and every group filled to at least 60 % at some point.
            (await CountAsync(
                "SELECT count(*) FROM groups.groups g WHERE g.tenant_id = @tenant AND (SELECT count(*) FROM enrollments.enrollments e WHERE e.group_id = g.id AND e.status = 'Active') > g.capacity", t)).ShouldBe(0);
            (await CountAsync(
                "SELECT count(*) FROM groups.groups g WHERE g.tenant_id = @tenant AND (SELECT count(*) FROM enrollments.enrollments e WHERE e.group_id = g.id) < 0.6 * g.capacity", t)).ShouldBe(0);

            // Attendance: three months of sessions, and only for students enrolled on the session date.
            (await CountAsync("SELECT count(*) FROM attendance.sessions WHERE tenant_id = @tenant AND date < DATE '2026-10-14'", t)).ShouldBe(0);
            (await CountAsync("SELECT count(*) FROM attendance.sessions WHERE tenant_id = @tenant", t)).ShouldBeGreaterThan(150);
            var records = await CountAsync("SELECT count(*) FROM attendance.attendance_records WHERE tenant_id = @tenant", t);
            var present = await CountAsync("SELECT count(*) FROM attendance.attendance_records WHERE tenant_id = @tenant AND status = 'Present'", t);
            ((double)present / records).ShouldBeInRange(0.75, 0.97);
            (await CountAsync(
                """
                SELECT count(*) FROM attendance.attendance_records r
                JOIN attendance.sessions s ON s.id = r.session_id
                WHERE r.tenant_id = @tenant AND NOT EXISTS (
                    SELECT 1 FROM enrollments.enrollments e
                    WHERE e.student_id = r.student_id AND e.group_id = s.group_id
                      AND e.start_date <= s.date AND (e.end_date IS NULL OR e.end_date >= s.date))
                """, t)).ShouldBe(0);

            // Billing: payments, exactly one corrected mistake, unpaid dues left, and announcements.
            (await CountAsync("SELECT count(*) FROM billing.payments WHERE tenant_id = @tenant AND status = 'Recorded'", t)).ShouldBeGreaterThan(200);
            (await CountAsync("SELECT count(*) FROM billing.payments WHERE tenant_id = @tenant AND status = 'Cancelled'", t)).ShouldBe(1);
            var dues = await AskAsAdminAsync<ListOutstandingDuesQuery, PagedResult<OutstandingDueDto>>(host, t, new ListOutstandingDuesQuery(null, null, new PageRequest(1, 1)));
            dues.TotalCount.ShouldBeGreaterThan(10);
            (await CountAsync("SELECT count(*) FROM announcements.announcements WHERE tenant_id = @tenant", t)).ShouldBeGreaterThanOrEqualTo(3);

            // No coach has two groups at the same time.
            var groups = await AskAsAdminAsync<ListGroupsQuery, PagedResult<GroupDto>>(host, t, new ListGroupsQuery(null, null, GroupStatus.Active, new PageRequest(1, 100)));
            var slots = groups.Items.SelectMany(g => g.Slots.Select(s => (Coach: g.Coach!.Id, s.Day, Start: TimeOnly.Parse(s.Start, CultureInfo.InvariantCulture), End: TimeOnly.Parse(s.End, CultureInfo.InvariantCulture)))).ToList();
            slots.ShouldAllBe(a => !slots.Any(b => !a.Equals(b) && a.Coach == b.Coach && a.Day == b.Day && a.Start < b.End && b.Start < a.End));
        }

        // Demo accounts sign in with the shared password and are not forced to change it.
        var login = await Client.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@kadikoy-spor.test", password = Password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsJsonAsync("/api/v1/auth/login", new { email = "antrenor1@bornova-genclik.test", password = Password }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Idempotent: a second run refuses and changes nothing.
        var students = await CountAsync("SELECT count(*) FROM students.students");
        (await seeder.RunAsync(new DemoSeedOptions(Now, Password: Password), Ct)).Created.ShouldBeFalse();
        (await CountAsync("SELECT count(*) FROM students.students")).ShouldBe(students);
    }

    [Fact]
    public async Task Never_runs_in_production()
    {
        using var host = SeedHost("Production");

        var exitCode = await DemoSeedProgram.RunAsync(host, new DemoSeedOptions(Now, Password: Password));

        exitCode.ShouldBe(DemoSeedProgram.WrongEnvironment);
        (await CountAsync("SELECT count(*) FROM tenancy.schools")).ShouldBe(0);
    }

    [Fact]
    public async Task Never_runs_against_a_remote_database()
    {
        var remote = new NpgsqlConnectionStringBuilder(Database.AppConnectionString) { Host = "db.example.com" }.ConnectionString;
        using var host = DemoSeedHost.Create([], new Dictionary<string, string?> { ["ConnectionStrings:App"] = remote }, "Development");

        var exitCode = await DemoSeedProgram.RunAsync(host, new DemoSeedOptions(Now, Password: Password));

        exitCode.ShouldBe(DemoSeedProgram.NotLocalDatabase);
        (await CountAsync("SELECT count(*) FROM tenancy.schools")).ShouldBe(0);
    }

    [Theory]
    [InlineData("Host=localhost;Port=55432;Database=x", true)]
    [InlineData("Host=127.0.0.1;Database=x", true)]
    [InlineData("Host=::1;Database=x", true)]
    [InlineData("Host=[::1]:55432;Database=x", true)]
    [InlineData("Host=LOCALHOST:5432;Database=x", true)]
    [InlineData("Host=db.example.com;Database=x", false)]
    [InlineData("Host=localhost,db.example.com;Database=x", false)]
    [InlineData("Host=10.0.0.5;Database=x", false)]
    [InlineData("Host=localhost.evil.example;Database=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_local_databases_are_accepted(string? connectionString, bool local)
    {
        DemoSeedProgram.IsLocalDatabase(connectionString).ShouldBe(local);
    }

    [Fact]
    public async Task A_weak_demo_password_is_refused_before_anything_is_written()
    {
        using var host = SeedHost();
        await using var scope = host.Services.CreateAsyncScope();

        await Should.ThrowAsync<DemoSeedException>(() =>
            scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(new DemoSeedOptions(Now, Password: "1234567890"), Ct));

        (await CountAsync("SELECT count(*) FROM tenancy.schools")).ShouldBe(0);
    }
}

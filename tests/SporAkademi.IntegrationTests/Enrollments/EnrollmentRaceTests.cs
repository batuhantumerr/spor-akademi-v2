using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Enrollments;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Infrastructure.Enrollments;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

/// <summary>
/// ADR-0005 races made deterministic: every contender is held right before its locks and again right after
/// its last read. With the real locks only one can get past the read at a time, so they run one after
/// another; without them (the controls, or a broken lock) all read the same count and overbook.
/// </summary>
[Trait("Story", "US-17")]
public sealed class EnrollmentRaceTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    private static readonly TimeSpan HardPatience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockedReadHold = TimeSpan.FromSeconds(1);

    private Gate? _lockGate;
    private Gate? _readGate;

    [Fact]
    public async Task Two_admins_racing_for_the_last_seat_only_one_wins()
    {
        var group = await GroupAsync("Son Yer", capacity: 1);
        var students = await StudentsAsync(4);
        await using var api = Gated(contenders: 4, realLocks: true);
        using var adminA = AdminOf(api);
        using var adminB = AdminOf(api);

        var responses = await Task.WhenAll(students.Select((s, i) => EnrollAsync(s, IdOf(group), client: i % 2 == 0 ? adminA : adminB)));
        ShouldHaveRaced();

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            (await CodeOf(rejected)).ShouldBe("Enrollment.GroupFull");
        }
    }

    [Fact]
    public async Task Control_without_locks_the_same_race_overbooks()
    {
        // Proves the gate really lines the contenders up: without the row locks every one of them wins.
        var group = await GroupAsync("Son Yer", capacity: 1);
        var students = await StudentsAsync(4);
        await using var api = Gated(contenders: 4, realLocks: false);
        using var admin = AdminOf(api);

        var responses = await Task.WhenAll(students.Select(s => EnrollAsync(s, IdOf(group), client: admin)));
        ShouldHaveRaced();

        responses.ShouldAllBe(r => r.StatusCode != HttpStatusCode.InternalServerError);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Same_student_enrolled_at_once_gets_one_enrollment_with_or_without_locks()
    {
        var student = await RegisterStudentAsync();
        foreach (var realLocks in new[] { true, false })
        {
            var group = await GroupAsync($"Grup {realLocks}");
            await using var api = Gated(contenders: 4, realLocks);
            using var admin = AdminOf(api);

            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => EnrollAsync(student, IdOf(group), client: admin)));
            ShouldHaveRaced();

            // With locks the policy refuses; without them the partial unique index still does (DEC-050).
            responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
            foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
            {
                rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                (await CodeOf(rejected)).ShouldBe("Enrollment.AlreadyEnrolled");
            }
        }
    }

    [Fact]
    [Trait("Story", "US-11")]
    public async Task Lowering_the_capacity_while_enrolling_never_overbooks()
    {
        var group = await GroupAsync("U10 Futbol", capacity: 2);
        await EnrollOkAsync(await RegisterStudentAsync("Ali"), IdOf(group));
        var newcomer = await RegisterStudentAsync("Veli");
        group = await GetGroupAsync(IdOf(group));
        await using var api = Gated(contenders: 2, realLocks: true);
        using var admin = AdminOf(api);

        var lower = admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", new
        {
            name = "U10 Futbol",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity = 1,
            facilityId = FieldA,
            version = VersionOf(group),
        }, Ct);
        var enroll = EnrollAsync(newcomer, IdOf(group), client: admin);
        var (lowered, enrolled) = (await lower, await enroll);
        ShouldHaveRaced();

        // Exactly one wins; whichever loses says why.
        (lowered.IsSuccessStatusCode ^ enrolled.IsSuccessStatusCode).ShouldBeTrue();
        if (lowered.IsSuccessStatusCode)
        {
            (await CodeOf(enrolled)).ShouldBe("Enrollment.GroupFull");
        }
        else
        {
            (await CodeOf(lowered)).ShouldBe("Group.CapacityBelowActiveEnrollments");
        }

        var capacity = (await GetGroupAsync(IdOf(group))).GetProperty("capacity").GetInt32();
        var active = (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/enrollments?status=Active", Ct)).GetProperty("totalCount").GetInt32();
        active.ShouldBeLessThanOrEqualTo(capacity);
    }

    [Fact]
    [Trait("Story", "US-15")]
    public async Task A_student_leaving_while_being_enrolled_never_keeps_an_active_enrollment()
    {
        var group = await GroupAsync("U10 Futbol");
        var students = await StudentsAsync(5);

        await Task.WhenAll(students.SelectMany(s => new[]
        {
            EnrollAsync(s, IdOf(group)),
            Admin.PostAsJsonAsync($"/api/v1/students/{s}/leave", new { date = Today }, Ct),
        }));

        foreach (var student in students)
        {
            var status = (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct)).GetProperty("status").GetString();
            var active = (await StudentEnrollmentsAsync(student)).EnumerateArray().Count(e => e.GetProperty("status").GetString() == "Active");
            if (status == "Left")
            {
                active.ShouldBe(0);
            }
        }
    }

    [Fact]
    [Trait("Story", "US-11")]
    public async Task Archiving_while_enrolling_never_leaves_an_active_enrollment_in_an_archived_group()
    {
        var group = await GroupAsync("U10 Futbol");
        var students = await StudentsAsync(4);

        await Task.WhenAll(students.Select(s => EnrollAsync(s, IdOf(group))).Append(ArchiveAsync(IdOf(group))));

        (await GetGroupAsync(IdOf(group))).GetProperty("status").GetString().ShouldBe("Archived");
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/enrollments?status=Active", Ct))
            .GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_lock_held_longer_than_the_timeout_is_a_409_lock_timeout()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");

        // Another connection holds the student's row lock past lock_timeout (5 s).
        await using var holder = new NpgsqlConnection(Database.MigratorConnectionString);
        await holder.OpenAsync(Ct);
        await using var transaction = await holder.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM students.students WHERE id = @id FOR UPDATE", holder, transaction))
        {
            command.Parameters.AddWithValue("id", student);
            await command.ExecuteNonQueryAsync(Ct);
        }

        var response = await EnrollAsync(student, IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Lock.Timeout");
        await transaction.RollbackAsync(Ct);
        (await EnrollAsync(student, IdOf(group))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Story", "US-18")]
    public async Task Enrollment_rows_are_version_checked()
    {
        var enrollment = EnrollmentId.From(IdOf(await EnrollOkAsync(await RegisterStudentAsync(), IdOf(await GroupAsync("U10")))));

        // A stale copy loaded before someone else ended the enrollment cannot overwrite it (xmin token).
        var result = await InScopeAsync(async sp =>
        {
            sp.GetRequiredService<BackgroundTenantContext>().Enter(School.Value);
            var db = sp.GetRequiredService<AppDbContext>();
            return await sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                async ct =>
                {
                    var stale = await db.Set<Enrollment>().SingleAsync(e => e.Id == enrollment, ct);
                    (await EndAsync(enrollment.Value, Today)).StatusCode.ShouldBe(HttpStatusCode.OK);
                    stale.End(new DateOnly(2027, 1, 14)).IsSuccess.ShouldBeTrue();
                    return Result.Success(true);
                },
                Ct);
        });

        result.Error.ShouldBe(PersistenceErrors.ConcurrencyConflict);
    }

    private async Task<List<Guid>> StudentsAsync(int count)
    {
        var students = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            students.Add(await RegisterStudentAsync($"Öğrenci{i}"));
        }

        return students;
    }

    /// <summary>
    /// An API whose contenders are lined up twice: all must reach the lock call before any locks (hard
    /// gate), and each is held right after its last read, the active count, until all have read (read gate).
    /// With real locks only the lock holder can get past its read, so the read gate gives up quietly after
    /// <see cref="LockedReadHold"/>; if the locks were missing, everyone would read the same count and the
    /// locked tests would fail. Without locks (the controls) the read gate is hard as well.
    /// </summary>
    private WebApplicationFactory<Program> Gated(int contenders, bool realLocks)
    {
        _lockGate = new Gate(contenders, "lock call", HardPatience, mustFill: true);
        _readGate = realLocks
            ? new Gate(contenders, "active count read", LockedReadHold, mustFill: false)
            : new Gate(contenders, "active count read", HardPatience, mustFill: true);
        var (lockGate, readGate) = (_lockGate, _readGate);
        return Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentLocks>(sp => new GatedEnrollmentLocks(
                realLocks ? ActivatorUtilities.CreateInstance<EnrollmentLocks>(sp) : new NoLocks(),
                lockGate)));
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentRepository>(sp => new ReadGatedEnrollments(
                ActivatorUtilities.CreateInstance<EnrollmentRepository>(sp), readGate)));
        }));
    }

    /// <summary>Fails with a readable message if a hard gate gave up waiting (the race did not happen as designed).</summary>
    private void ShouldHaveRaced()
    {
        _lockGate!.TimeoutMessage.ShouldBeNull();
        _readGate!.TimeoutMessage.ShouldBeNull();
    }

    private HttpClient AdminOf(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));
        return client;
    }

    /// <summary>
    /// Releases everyone once all contenders have arrived. A hard gate (<c>mustFill</c>) never lets anyone
    /// through early: after its patience the request fails and <see cref="TimeoutMessage"/> says why. A soft
    /// gate lets each waiter go after its patience, for contenders that are legitimately blocked elsewhere.
    /// </summary>
    private sealed class Gate(int contenders, string point, TimeSpan patience, bool mustFill)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public string? TimeoutMessage { get; private set; }

        public async Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= contenders)
            {
                _open.TrySetResult();
            }

            if (await Task.WhenAny(_open.Task, Task.Delay(patience)) == _open.Task || !mustFill)
            {
                return;
            }

            TimeoutMessage = $"Only {Volatile.Read(ref _arrived)} of {contenders} contenders reached the {point} within {patience}; the race did not run as designed.";
            throw new TimeoutException(TimeoutMessage);
        }
    }

    private sealed class GatedEnrollmentLocks(IEnrollmentLocks inner, Gate gate) : IEnrollmentLocks
    {
        public async Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken)
        {
            await gate.ArriveAndWaitAsync();
            await inner.LockStudentAndGroupAsync(studentId, groupId, cancellationToken);
        }

        public async Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken)
        {
            await gate.ArriveAndWaitAsync();
            await inner.LockGroupAsync(groupId, cancellationToken);
        }
    }

    private sealed class NoLocks : IEnrollmentLocks
    {
        public Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Holds each contender right after it counted the group's active enrollments, its last read before saving.</summary>
    private sealed class ReadGatedEnrollments(IEnrollmentRepository inner, Gate gate) : IEnrollmentRepository
    {
        public async Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken)
        {
            var count = await inner.CountActiveAsync(groupId, cancellationToken);
            await gate.ArriveAndWaitAsync();
            return count;
        }

        public Task<Enrollment?> GetAsync(EnrollmentId id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

        public void Add(Enrollment enrollment) => inner.Add(enrollment);

        public Task<IReadOnlyList<Enrollment>> ListActiveOfGroupAsync(GroupId groupId, CancellationToken cancellationToken) =>
            inner.ListActiveOfGroupAsync(groupId, cancellationToken);

        public Task<IReadOnlyList<Enrollment>> ListActiveOfStudentAsync(StudentId studentId, CancellationToken cancellationToken) =>
            inner.ListActiveOfStudentAsync(studentId, cancellationToken);

        public Task<Enrollment?> FindAsync(EnrollmentId id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);

        public Task<PagedResult<Enrollment>> ListOfGroupAsync(GroupId groupId, EnrollmentStatus? status, PageRequest page, CancellationToken cancellationToken) =>
            inner.ListOfGroupAsync(groupId, status, page, cancellationToken);

        public Task<IReadOnlyList<Enrollment>> ListOfStudentAsync(StudentId studentId, CancellationToken cancellationToken) =>
            inner.ListOfStudentAsync(studentId, cancellationToken);

        public Task<IReadOnlyList<StudentId>> ActiveStudentsOfGroupsAsync(IReadOnlyCollection<GroupId> groupIds, CancellationToken cancellationToken) =>
            inner.ActiveStudentsOfGroupsAsync(groupIds, cancellationToken);

        public Task<IReadOnlyList<Enrollment>> ListOfStudentsAsync(IReadOnlyCollection<StudentId> studentIds, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<Enrollment>> ListOfGroupBetweenAsync(GroupId groupId, DateOnly firstDay, DateOnly lastDay, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}

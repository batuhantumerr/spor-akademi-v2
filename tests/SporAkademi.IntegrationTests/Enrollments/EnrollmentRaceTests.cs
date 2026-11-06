using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SporAkademi.Application.Abstractions.Data;
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
/// ADR-0005 races made deterministic: every contender is held at a gate right before its locks and again
/// right after them, so all of them have read nothing before the first one locks. With the real locks
/// they then run one after another; without locks (the control tests) they all read the same counts.
/// </summary>
[Trait("Story", "US-17")]
public sealed class EnrollmentRaceTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    [Fact]
    public async Task Two_admins_racing_for_the_last_seat_only_one_wins()
    {
        var group = await GroupAsync("Son Yer", capacity: 1);
        var students = await StudentsAsync(4);
        await using var api = Gated(contenders: 4, realLocks: true);
        using var adminA = AdminOf(api);
        using var adminB = AdminOf(api);

        var responses = await Task.WhenAll(students.Select((s, i) => EnrollAsync(s, IdOf(group), client: i % 2 == 0 ? adminA : adminB)));

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

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(4);
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
        await using var holder = new NpgsqlConnection(Database.AppConnectionString);
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
            var db = sp.GetRequiredService<AppDbContext>();
            return await sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                async ct =>
                {
                    var stale = await db.Set<Enrollment>().IgnoreQueryFilters().SingleAsync(e => e.Id == enrollment, ct);
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

    private WebApplicationFactory<Program> Gated(int contenders, bool realLocks)
    {
        var gate = new LockGate(contenders);
        return Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentLocks>(sp => new GatedEnrollmentLocks(
                realLocks ? ActivatorUtilities.CreateInstance<EnrollmentLocks>(sp) : new NoLocks(),
                gate)))));
    }

    private HttpClient AdminOf(WebApplicationFactory<Program> api)
    {
        var client = api.CreateClient();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));
        return client;
    }

    /// <summary>
    /// Lines contenders up: all must reach the lock call before any takes a lock, and after locking each
    /// waits until all have locked or a short while has passed (with real locks only one can).
    /// </summary>
    private sealed class LockGate(int contenders)
    {
        private static readonly TimeSpan HoldAfterLock = TimeSpan.FromMilliseconds(500);
        private readonly TaskCompletionSource _beforeLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _afterLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _locked;

        public async Task BeforeLockAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= contenders)
            {
                _beforeLock.TrySetResult();
            }

            await _beforeLock.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task AfterLockAsync()
        {
            if (Interlocked.Increment(ref _locked) >= contenders)
            {
                _afterLock.TrySetResult();
            }

            await Task.WhenAny(_afterLock.Task, Task.Delay(HoldAfterLock));
            _afterLock.TrySetResult();
        }
    }

    private sealed class GatedEnrollmentLocks(IEnrollmentLocks inner, LockGate gate) : IEnrollmentLocks
    {
        public async Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken)
        {
            await gate.BeforeLockAsync();
            await inner.LockStudentAndGroupAsync(studentId, groupId, cancellationToken);
            await gate.AfterLockAsync();
        }

        public async Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken)
        {
            await gate.BeforeLockAsync();
            await inner.LockGroupAsync(groupId, cancellationToken);
            await gate.AfterLockAsync();
        }
    }

    private sealed class NoLocks : IEnrollmentLocks
    {
        public Task LockStudentAndGroupAsync(StudentId studentId, GroupId groupId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LockGroupAsync(GroupId groupId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

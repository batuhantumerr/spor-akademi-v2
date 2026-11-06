using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

[Trait("Story", "US-11")]
public sealed class EditAndArchiveTests(DatabaseFixture database) : GroupsTest(database)
{
    private object Edit(JsonElement group, string name = "U11 Futbol", int capacity = 25, Guid? facilityId = null, Guid? coachId = null, uint? version = null) => new
    {
        name,
        birthYearRange = new { from = 2014, to = 2015 },
        capacity,
        facilityId = facilityId ?? FieldA,
        coachId,
        version = version ?? VersionOf(group),
    };

    [Fact]
    public async Task Edit_changes_name_birth_years_capacity_facility_and_coach()
    {
        var group = await CreateGroupAsync(coach: Ayse);
        var pool = await CreateFacilityAsync(Admin, "Havuz");

        var response = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group, facilityId: pool, coachId: Mehmet.Id.Value), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await BodyOf(response);
        updated.GetProperty("name").GetString().ShouldBe("U11 Futbol");
        updated.GetProperty("birthYearRange").GetProperty("from").GetInt32().ShouldBe(2014);
        updated.GetProperty("capacity").GetInt32().ShouldBe(25);
        updated.GetProperty("facility").GetProperty("name").GetString().ShouldBe("Havuz");
        updated.GetProperty("coach").GetProperty("id").GetGuid().ShouldBe(Mehmet.Id.Value);
        VersionOf(updated).ShouldNotBe(VersionOf(group));
    }

    [Fact]
    public async Task Edit_with_a_stale_version_is_a_conflict()
    {
        var group = await CreateGroupAsync();
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stale = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group, name: "Başka"), Ct);

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(stale)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task Edit_keeps_an_archived_facility_that_was_already_assigned()
    {
        var group = await CreateGroupAsync();
        await Admin.PostAsync(new Uri($"/api/v1/facilities/{FieldA}/archive", UriKind.Relative), null, Ct);

        (await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Capacity_cannot_drop_below_active_enrollments()
    {
        // Enrollments arrive in P1-26; the read contract is replaced to simulate five active students.
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentCounts>(_ => new FixedEnrollmentCounts(5)))));
        using var admin = api.CreateClient();
        admin.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));
        var group = await CreateGroupAsync();

        var below = await admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group, capacity: 4), Ct);
        var equal = await admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group, capacity: 5), Ct);

        below.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await BodyOf(below);
        problem.GetProperty("code").GetString().ShouldBe("Group.CapacityBelowActiveEnrollments");
        problem.GetProperty("args").GetProperty("count").GetInt32().ShouldBe(5);
        equal.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Archive_uses_todays_date_in_the_schools_time_zone_and_ends_enrollments()
    {
        var group = await CreateGroupAsync();
        // 22:30 UTC on 14 January is already 01:30 on 15 January in Istanbul.
        Api.Clock.SetUtcNow(new DateTimeOffset(2027, 1, 14, 22, 30, 0, TimeSpan.Zero));
        var ended = new RecordingEnrollmentEnder();
        using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentEnder>(_ => ended))));
        using var admin = api.CreateClient();
        admin.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));

        var response = await ArchiveAsync(IdOf(group), admin);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var archived = await BodyOf(response);
        archived.GetProperty("status").GetString().ShouldBe("Archived");
        archived.GetProperty("archivedOn").GetString().ShouldBe("2027-01-15");
        ended.Calls.ShouldBe([(GroupId.From(IdOf(group)), new DateOnly(2027, 1, 15))]);
    }

    [Fact]
    public async Task Archived_group_cannot_be_edited_rescheduled_or_archived_again()
    {
        var group = await CreateGroupAsync();
        await ArchiveAsync(IdOf(group));
        group = await GetGroupAsync(IdOf(group));

        var edit = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(group), Ct);
        var schedule = await PutScheduleAsync(group, null, Slot("Monday", "17:00", "18:00"));
        var again = await ArchiveAsync(IdOf(group));

        foreach (var response in new[] { edit, schedule, again })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CodeOf(response)).ShouldBe("Group.Archived");
        }
    }

    [Fact]
    public async Task Archived_group_disappears_from_the_coach_screens()
    {
        var group = await ScheduleAsync(await CreateGroupAsync(coach: Ayse), Slot("Thursday", "17:00", "18:00"));
        using var coach = CoachClient(Ayse);
        (await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/groups", Ct)).GetArrayLength().ShouldBe(1);

        await ArchiveAsync(IdOf(group));

        (await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/groups", Ct)).GetArrayLength().ShouldBe(0);
        (await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions?date=2027-01-21", Ct)).GetArrayLength().ShouldBe(0);
        (await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetGroupAsync(IdOf(group))).GetProperty("status").GetString().ShouldBe("Archived");
    }

    [Fact]
    [Trait("Story", "US-07")]
    public async Task Facility_of_an_active_group_cannot_be_deleted()
    {
        await CreateGroupAsync();

        var response = await Admin.DeleteAsync(new Uri($"/api/v1/facilities/{FieldA}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Facility.InUse");
    }

    [Fact]
    [Trait("Story", "US-07")]
    public async Task Facility_kept_by_archived_groups_is_in_use_through_the_foreign_key()
    {
        var group = await CreateGroupAsync();
        await ArchiveAsync(IdOf(group));

        var response = await Admin.DeleteAsync(new Uri($"/api/v1/facilities/{FieldA}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Facility.InUse");
    }

    [Fact]
    [Trait("Story", "US-06")]
    public async Task Sport_with_groups_cannot_be_deleted_but_an_unused_one_can()
    {
        var group = await CreateGroupAsync();
        await ArchiveAsync(IdOf(group));
        var unused = await CreateSportAsync(Admin, "Tenis");

        var inUse = await Admin.DeleteAsync(new Uri($"/api/v1/sports/{Football}", UriKind.Relative), Ct);

        inUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(inUse)).ShouldBe("Sport.InUse");
        (await Admin.DeleteAsync(new Uri($"/api/v1/sports/{unused}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private sealed class FixedEnrollmentCounts(int count) : IEnrollmentCounts
    {
        public Task<int> CountActiveAsync(GroupId groupId, CancellationToken cancellationToken) => Task.FromResult(count);
    }

    private sealed class RecordingEnrollmentEnder : IEnrollmentEnder
    {
        public List<(GroupId GroupId, DateOnly EndDate)> Calls { get; } = [];

        public Task<Result> EndActiveEnrollmentsAsync(GroupId groupId, DateOnly endDate, CancellationToken cancellationToken)
        {
            Calls.Add((groupId, endDate));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> EndActiveEnrollmentsOfStudentAsync(Domain.Students.StudentId studentId, DateOnly endDate, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}

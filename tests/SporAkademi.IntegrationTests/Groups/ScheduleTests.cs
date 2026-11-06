using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

[Trait("Story", "US-10")]
public sealed class ScheduleTests(DatabaseFixture database) : GroupsTest(database)
{
    [Fact]
    public async Task Slots_are_saved_and_returned_in_school_local_time()
    {
        var group = await CreateGroupAsync(coach: Ayse);

        var response = await PutScheduleAsync(group, null, Slot("Tuesday", "17:00", "18:30"), Slot("Thursday", "17:00", "18:30"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await BodyOf(response);
        body.GetProperty("id").GetGuid().ShouldBe(IdOf(group));
        body.GetProperty("warnings").GetArrayLength().ShouldBe(0);
        VersionOf(body).ShouldNotBe(VersionOf(group));
        var slots = (await GetGroupAsync(IdOf(group))).GetProperty("slots").EnumerateArray()
            .Select(s => $"{s.GetProperty("day").GetString()} {s.GetProperty("start").GetString()}-{s.GetProperty("end").GetString()}");
        slots.ShouldBe(["Tuesday 17:00-18:30", "Thursday 17:00-18:30"]);
    }

    [Fact]
    public async Task End_not_after_start_is_rejected()
    {
        var group = await CreateGroupAsync();

        var response = await PutScheduleAsync(group, null, Slot("Monday", "18:00", "18:00"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("slots[0].end")[0].GetString().ShouldBe("TimeRange.Invalid");
    }

    [Fact]
    public async Task Malformed_times_are_field_errors()
    {
        var group = await CreateGroupAsync();

        var response = await PutScheduleAsync(group, null, Slot("Monday", "7pm", "19:00:00"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await BodyOf(response)).GetProperty("errors");
        errors.GetProperty("slots[0].start")[0].GetString().ShouldBe("Field.Invalid");
        errors.GetProperty("slots[0].end")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task Overlapping_slots_of_the_same_group_are_rejected()
    {
        var group = await CreateGroupAsync();

        var response = await PutScheduleAsync(group, null, Slot("Monday", "17:00", "18:30"), Slot("Monday", "18:00", "19:00"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Schedule.SlotOverlap");
        (await GetGroupAsync(IdOf(group))).GetProperty("slots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Facility_overlap_with_another_group_warns_but_still_saves()
    {
        var other = await CreateGroupAsync("U12 Futbol", Mehmet);
        await ScheduleAsync(other, Slot("Monday", "17:00", "18:00"));
        var group = await CreateGroupAsync("U10 Futbol", Ayse);

        var response = await PutScheduleAsync(group, null, Slot("Monday", "17:30", "18:30"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var warning = (await BodyOf(response)).GetProperty("warnings").EnumerateArray().ShouldHaveSingleItem();
        warning.GetProperty("code").GetString().ShouldBe("Schedule.FacilityOverlap");
        warning.GetProperty("relatedId").GetGuid().ShouldBe(IdOf(other));
        warning.GetProperty("args").GetProperty("groupName").GetString().ShouldBe("U12 Futbol");
        (await GetGroupAsync(IdOf(group))).GetProperty("slots").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Coach_with_another_group_at_that_time_is_rejected_across_facilities()
    {
        var pool = await CreateFacilityAsync(Admin, "Havuz");
        var other = await CreateGroupAsync("Yüzme Başlangıç", Ayse, pool);
        await ScheduleAsync(other, Slot("Wednesday", "10:00", "11:00"));
        var group = await CreateGroupAsync("U10 Futbol", Ayse);

        var response = await PutScheduleAsync(group, null, Slot("Wednesday", "10:30", "11:30"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await BodyOf(response);
        problem.GetProperty("code").GetString().ShouldBe("Schedule.CoachUnavailable");
        problem.GetProperty("args").GetProperty("groupName").GetString().ShouldBe("Yüzme Başlangıç");
        (await GetGroupAsync(IdOf(group))).GetProperty("slots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Coach_can_train_groups_back_to_back_and_archived_groups_do_not_block()
    {
        var earlier = await CreateGroupAsync("Sabah", Ayse);
        await ScheduleAsync(earlier, Slot("Friday", "09:00", "10:00"));
        var old = await CreateGroupAsync("Eski", Ayse);
        await ScheduleAsync(old, Slot("Friday", "10:00", "11:00"));
        await ArchiveAsync(IdOf(old));
        var group = await CreateGroupAsync("Öğle", Ayse);

        var response = await PutScheduleAsync(group, null, Slot("Friday", "10:00", "11:00"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("warnings").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Assigning_a_busy_coach_to_a_scheduled_group_is_rejected()
    {
        var mehmetsGroup = await CreateGroupAsync("Mehmet'in Grubu", Mehmet);
        await ScheduleAsync(mehmetsGroup, Slot("Saturday", "10:00", "11:00"));
        var group = await ScheduleAsync(await CreateGroupAsync("U10 Futbol", Ayse), Slot("Saturday", "10:00", "11:00"));

        var response = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", new
        {
            name = "U10 Futbol",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity = 20,
            facilityId = FieldA,
            coachId = Mehmet.Id.Value,
            version = VersionOf(group),
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Schedule.CoachUnavailable");
    }

    [Fact]
    public async Task Stale_version_is_a_conflict()
    {
        var group = await CreateGroupAsync();
        await ScheduleAsync(group, Slot("Monday", "17:00", "18:00"));

        var response = await PutScheduleAsync(group, VersionOf(group), Slot("Tuesday", "17:00", "18:00"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Concurrency.Conflict");
    }

    [Fact]
    public async Task Empty_schedule_clears_the_slots()
    {
        var group = await ScheduleAsync(await CreateGroupAsync(), Slot("Monday", "17:00", "18:00"));

        var response = await PutScheduleAsync(group);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BodyOf(response)).GetProperty("slots").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Planned_sessions_of_a_group_come_from_its_schedule()
    {
        var group = await ScheduleAsync(await CreateGroupAsync(), Slot("Tuesday", "17:00", "18:30"), Slot("Thursday", "17:00", "18:30"));

        var sessions = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/sessions?from=2027-01-11&to=2027-01-17", Ct);

        sessions.EnumerateArray().Select(s => $"{s.GetProperty("date").GetString()} {s.GetProperty("start").GetString()}-{s.GetProperty("end").GetString()}")
            .ShouldBe(["2027-01-12 17:00-18:30", "2027-01-14 17:00-18:30"]);
        sessions[0].GetProperty("facility").GetProperty("name").GetString().ShouldBe("A Sahası");
        sessions[0].GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
    }

    [Theory]
    [InlineData("from=2027-01-01&to=2027-03-04", "to")]
    [InlineData("from=2027-01-10&to=2027-01-09", "to")]
    [InlineData("to=2027-01-09", "from")]
    public async Task Session_window_must_be_ordered_and_at_most_62_days(string query, string field)
    {
        var group = await CreateGroupAsync();

        var response = await Admin.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/sessions?{query}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_62_day_window_is_allowed()
    {
        var group = await CreateGroupAsync();

        (await Admin.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/sessions?from=2027-01-01&to=2027-03-03", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

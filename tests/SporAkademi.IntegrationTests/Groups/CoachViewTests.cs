using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Groups;

[Trait("Story", "US-12")]
public sealed class CoachViewTests(DatabaseFixture database) : GroupsTest(database)
{
    private static string[] Sessions(JsonElement sessions) =>
        [.. sessions.EnumerateArray().Select(s => $"{s.GetProperty("date").GetString()} {s.GetProperty("start").GetString()} {s.GetProperty("groupName").GetString()}")];

    [Fact]
    public async Task Coach_sees_only_own_active_groups()
    {
        await CreateGroupAsync("B Grubu", Ayse);
        await CreateGroupAsync("A Grubu", Ayse);
        await CreateGroupAsync("Mehmet'in Grubu", Mehmet);
        using var coach = CoachClient(Ayse);

        var mine = await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/groups", Ct);

        mine.EnumerateArray().Select(g => g.GetProperty("name").GetString()).ShouldBe(["A Grubu", "B Grubu"]);
    }

    [Fact]
    public async Task Coach_gets_own_group_but_not_found_for_anyone_elses()
    {
        var own = await CreateGroupAsync("A Grubu", Ayse);
        var others = await CreateGroupAsync("Mehmet'in Grubu", Mehmet);
        var unassigned = await CreateGroupAsync("Boş Grup");
        using var coach = CoachClient(Ayse);

        (await coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(own)}", Ct)).GetProperty("name").GetString().ShouldBe("A Grubu");
        foreach (var id in new[] { IdOf(others), IdOf(unassigned), Guid.CreateVersion7() })
        {
            var response = await coach.GetAsync(new Uri($"/api/v1/groups/{id}", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("Group.NotFound");
            (await coach.GetAsync(new Uri($"/api/v1/groups/{id}/sessions?from=2027-01-11&to=2027-01-17", UriKind.Relative), Ct))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(own)}/sessions?from=2027-01-11&to=2027-01-17", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Todays_sessions_follow_the_schools_time_zone()
    {
        // The test clock starts at 2027-01-14 12:00 UTC, a Thursday afternoon in Istanbul.
        await ScheduleAsync(await CreateGroupAsync("Perşembe", Ayse), Slot("Thursday", "17:00", "18:00"));
        await ScheduleAsync(await CreateGroupAsync("Cuma", Ayse), Slot("Friday", "09:00", "10:00"));

        using (var coach = CoachClient(Ayse))
        {
            Sessions(await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions", Ct)).ShouldBe(["2027-01-14 17:00 Perşembe"]);
        }

        // 22:30 UTC is already Friday 01:30 in Istanbul (UTC+3).
        Api.Clock.SetUtcNow(new DateTimeOffset(2027, 1, 14, 22, 30, 0, TimeSpan.Zero));
        using var later = CoachClient(Ayse);
        Sessions(await later.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions", Ct)).ShouldBe(["2027-01-15 09:00 Cuma"]);
    }

    [Fact]
    public async Task Sessions_of_a_chosen_day_span_all_own_groups_in_start_order()
    {
        await ScheduleAsync(await CreateGroupAsync("Akşam", Ayse), Slot("Monday", "18:00", "19:00"));
        await ScheduleAsync(await CreateGroupAsync("Sabah", Ayse), Slot("Monday", "09:00", "10:00"), Slot("Tuesday", "09:00", "10:00"));
        await ScheduleAsync(await CreateGroupAsync("Mehmet'in Grubu", Mehmet), Slot("Monday", "12:00", "13:00"));
        using var coach = CoachClient(Ayse);

        var sessions = await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/sessions?date=2027-01-18", Ct);

        Sessions(sessions).ShouldBe(["2027-01-18 09:00 Sabah", "2027-01-18 18:00 Akşam"]);
        sessions[0].GetProperty("end").GetString().ShouldBe("10:00");
        sessions[0].GetProperty("facility").GetProperty("name").GetString().ShouldBe("A Sahası");
    }

    [Fact]
    public async Task Coach_screens_are_for_coaches_only()
    {
        (await Admin.GetAsync(new Uri("/api/v1/me/groups", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Admin.GetAsync(new Uri("/api/v1/me/sessions", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Coach_cannot_change_own_group()
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        using var coach = CoachClient(Ayse);

        (await ArchiveAsync(IdOf(group), coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coach.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}/schedule", new { slots = Array.Empty<object>(), version = VersionOf(group) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Story", "US-08")]
    public async Task Deactivated_coach_leaves_the_groups_without_a_coach()
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        var other = await CreateGroupAsync("Mehmet'in Grubu", Mehmet);

        var response = await Admin.PostAsync(new Uri($"/api/v1/coaches/{Ayse.Id.Value}/deactivate", UriKind.Relative), null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await GetGroupAsync(IdOf(group));
        after.GetProperty("coach").ValueKind.ShouldBe(JsonValueKind.Null);
        after.GetProperty("status").GetString().ShouldBe("Active");
        (await GetGroupAsync(IdOf(other))).GetProperty("coach").GetProperty("id").GetGuid().ShouldBe(Mehmet.Id.Value);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Attendance;

/// <summary>
/// P2-F5, DEC-363: once a student is anonymized, the coach endpoints that show past attendance name no one and the
/// roster no longer lists the student.
/// </summary>
[Trait("Story", "P2-F5")]
public sealed class AnonymizedStudentCoachViewTests(DatabaseFixture database) : AttendanceTest(database)
{
    [Fact]
    public async Task Coach_views_of_past_attendance_show_only_the_anonymous_name()
    {
        await SaveOkAsync(Admin, ThisMorning, (Ali, "Present"), (Veli, "Absent"));
        (await Admin.PostAsJsonAsync($"/api/v1/students/{Ali}/leave", new { date = "2027-01-14" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Api.Clock.SetUtcNow(new DateTimeOffset(2029, 2, 1, 9, 0, 0, TimeSpan.Zero));
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<StudentAnonymizationJob>().RunAsync(Ct)).ShouldBe(1);
        }

        using var coach = Api.CoachClient(School, Ayse.Id.Value);
        var month = await coach.GetAsync(new Uri($"/api/v1/groups/{GroupId}/attendance?month=2027-01", UriKind.Relative), Ct);
        var session = await coach.GetAsync(new Uri(SessionUrl(ThisMorning), UriKind.Relative), Ct);
        var history = await coach.GetAsync(new Uri($"/api/v1/students/{Ali}/attendance?from=2027-01-01&to=2027-01-31", UriKind.Relative), Ct);
        var roster = await coach.GetAsync(new Uri($"/api/v1/groups/{GroupId}/roster", UriKind.Relative), Ct);

        month.StatusCode.ShouldBe(HttpStatusCode.OK);
        session.StatusCode.ShouldBe(HttpStatusCode.OK);
        roster.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The student left the coach's group, so the student's own history is "not found" like any other student's.
        history.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        var monthBody = await month.Content.ReadAsStringAsync(Ct);
        var sessionBody = await session.Content.ReadAsStringAsync(Ct);
        monthBody.ShouldContain("Anonim");
        monthBody.ShouldContain(Ali.ToString());
        sessionBody.ShouldContain(Ali.ToString());
        foreach (var body in new[] { monthBody, sessionBody, await history.Content.ReadAsStringAsync(Ct), await roster.Content.ReadAsStringAsync(Ct) })
        {
            body.ShouldNotContain("Yılmaz");
        }

        (await roster.Content.ReadAsStringAsync(Ct)).ShouldNotContain("Anonim");
        monthBody.ShouldContain("Çelik");
    }
}

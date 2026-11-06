using System.Net;
using System.Net.Http.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

[Trait("Story", "US-17")]
public sealed class EnrollStudentTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    [Fact]
    public async Task Eligible_student_is_enrolled_from_today()
    {
        var student = await RegisterStudentAsync("Ali", "Deneme");
        var group = await GroupAsync("U10 Futbol");

        var response = await EnrollAsync(student, IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var enrollment = await BodyOf(response);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/enrollments/{IdOf(enrollment)}");
        enrollment.GetProperty("status").GetString().ShouldBe("Active");
        enrollment.GetProperty("startDate").GetString().ShouldBe(Today);
        enrollment.GetProperty("studentFirstName").GetString().ShouldBe("Ali");
        enrollment.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        (await Admin.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/enrollments/{IdOf(enrollment)}", Ct))
            .GetProperty("studentId").GetGuid().ShouldBe(student);
    }

    [Fact]
    public async Task Start_date_may_be_in_the_past_but_not_in_the_future()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");

        var future = await EnrollAsync(student, IdOf(group), "2027-01-15");
        var past = await EnrollAsync(student, IdOf(group), "2027-01-04");

        future.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(future)).GetProperty("errors").GetProperty("startDate")[0].GetString().ShouldBe("Field.Invalid");
        past.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await BodyOf(past)).GetProperty("startDate").GetString().ShouldBe("2027-01-04");
    }

    [Fact]
    public async Task Full_group_is_rejected()
    {
        var group = await GroupAsync("U10 Futbol", capacity: 1);
        await EnrollOkAsync(await RegisterStudentAsync("Ali"), IdOf(group));

        var response = await EnrollAsync(await RegisterStudentAsync("Veli"), IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Enrollment.GroupFull");
    }

    [Fact]
    public async Task Birth_year_outside_the_groups_range_is_rejected()
    {
        var student = await RegisterStudentAsync(birthDate: "2013-02-01");
        var group = await GroupAsync("U10 Futbol");

        var response = await EnrollAsync(student, IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await BodyOf(response);
        problem.GetProperty("code").GetString().ShouldBe("Enrollment.BirthYearOutOfRange");
        problem.GetProperty("args").GetProperty("year").GetInt32().ShouldBe(2013);
        problem.GetProperty("args").GetProperty("from").GetInt32().ShouldBe(2015);
        problem.GetProperty("args").GetProperty("to").GetInt32().ShouldBe(2016);
    }

    [Fact]
    public async Task Student_already_actively_enrolled_in_the_group_is_rejected()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");
        await EnrollOkAsync(student, IdOf(group));

        var response = await EnrollAsync(student, IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Enrollment.AlreadyEnrolled");
    }

    [Fact]
    public async Task Student_can_rejoin_a_group_after_the_enrollment_ended()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");
        var first = await EnrollOkAsync(student, IdOf(group), "2027-01-04");
        (await EndAsync(IdOf(first), "2027-01-10")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EnrollAsync(student, IdOf(group))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Overlapping_schedule_with_another_active_group_names_that_group()
    {
        var student = await RegisterStudentAsync();
        var swimming = await GroupAsync("Yüzme Başlangıç", 20, null, Slot("Tuesday", "17:00", "18:00"));
        await EnrollOkAsync(student, IdOf(swimming));
        var football = await GroupAsync("U10 Futbol", 20, null, Slot("Tuesday", "17:30", "18:30"));

        var response = await EnrollAsync(student, IdOf(football));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await BodyOf(response);
        problem.GetProperty("code").GetString().ShouldBe("Enrollment.ScheduleConflict");
        problem.GetProperty("args").GetProperty("groupName").GetString().ShouldBe("Yüzme Başlangıç");
    }

    [Fact]
    public async Task Student_can_be_in_several_groups_without_overlap()
    {
        var student = await RegisterStudentAsync();
        await EnrollOkAsync(student, IdOf(await GroupAsync("Yüzme", 20, null, Slot("Tuesday", "17:00", "18:00"))));

        (await EnrollAsync(student, IdOf(await GroupAsync("Futbol", 20, null, Slot("Tuesday", "18:00", "19:00"))))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        (await StudentEnrollmentsAsync(student)).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Archived_group_takes_no_enrollments()
    {
        var group = await GroupAsync("Eski Grup");
        await ArchiveAsync(IdOf(group));

        var response = await EnrollAsync(await RegisterStudentAsync(), IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Enrollment.GroupArchived");
    }

    [Fact]
    [Trait("Story", "US-15")]
    public async Task Student_who_left_cannot_be_enrolled_until_reactivated()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");
        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = Today }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await EnrollAsync(student, IdOf(group));
        (await Admin.PostAsync(new Uri($"/api/v1/students/{student}/reactivate", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var accepted = await EnrollAsync(student, IdOf(group));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(refused)).ShouldBe("Enrollment.StudentNotActive");
        accepted.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Unknown_student_or_group_is_not_found()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");

        (await CodeOf(await EnrollAsync(Guid.CreateVersion7(), IdOf(group)))).ShouldBe("Student.NotFound");
        (await CodeOf(await EnrollAsync(student, Guid.CreateVersion7()))).ShouldBe("Group.NotFound");
    }

    [Fact]
    public async Task Coaches_cannot_enroll()
    {
        using var coach = CoachClient(Ayse);

        (await EnrollAsync(await RegisterStudentAsync(), IdOf(await GroupAsync("U10")), client: coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

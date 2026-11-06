using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

[Trait("Story", "US-18")]
public sealed class EndEnrollmentTests(DatabaseFixture database) : EnrollmentsTest(database)
{
    [Fact]
    public async Task Ended_enrollment_frees_the_seat()
    {
        var group = await GroupAsync("U10 Futbol", capacity: 1);
        var first = await EnrollOkAsync(await RegisterStudentAsync("Ali"), IdOf(group), "2027-01-04");
        var waiting = await RegisterStudentAsync("Veli");
        (await EnrollAsync(waiting, IdOf(group))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var response = await EndAsync(IdOf(first), "2027-01-13");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ended = await BodyOf(response);
        ended.GetProperty("status").GetString().ShouldBe("Ended");
        ended.GetProperty("endDate").GetString().ShouldBe("2027-01-13");
        (await EnrollAsync(waiting, IdOf(group))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Ended_enrollment_stays_in_the_history()
    {
        var student = await RegisterStudentAsync();
        var group = await GroupAsync("U10 Futbol");
        var enrollment = await EnrollOkAsync(student, IdOf(group), "2027-01-04");
        await EndAsync(IdOf(enrollment), Today);

        var history = await StudentEnrollmentsAsync(student);
        var ofGroup = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/enrollments?status=Ended", Ct);
        var active = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/enrollments?status=Active", Ct);

        var item = history.EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("status").GetString().ShouldBe("Ended");
        item.GetProperty("startDate").GetString().ShouldBe("2027-01-04");
        item.GetProperty("endDate").GetString().ShouldBe(Today);
        ofGroup.GetProperty("totalCount").GetInt32().ShouldBe(1);
        active.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Ending_twice_or_before_the_start_is_rejected()
    {
        var enrollment = await EnrollOkAsync(await RegisterStudentAsync(), IdOf(await GroupAsync("U10")), "2027-01-10");

        var beforeStart = await EndAsync(IdOf(enrollment), "2027-01-09");
        (await EndAsync(IdOf(enrollment), "2027-01-10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var twice = await EndAsync(IdOf(enrollment), Today);

        (await CodeOf(beforeStart)).ShouldBe("Enrollment.InvalidEndDate");
        beforeStart.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(twice)).ShouldBe("Enrollment.AlreadyEnded");
    }

    [Theory]
    [InlineData(null, "Field.Required")]
    [InlineData("2027-01-15", "Field.Invalid")]
    [InlineData("1999-12-31", "Field.Invalid")]
    public async Task End_date_is_required_and_not_in_the_future(string? date, string code)
    {
        var enrollment = await EnrollOkAsync(await RegisterStudentAsync(), IdOf(await GroupAsync("U10")));

        var response = await EndAsync(IdOf(enrollment), date);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("date")[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Unknown_enrollment_is_not_found()
    {
        var response = await EndAsync(Guid.CreateVersion7(), Today);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Enrollment.NotFound");
    }

    [Fact]
    [Trait("Story", "US-15")]
    public async Task Leaving_ends_every_active_enrollment_on_the_leave_date()
    {
        var student = await RegisterStudentAsync();
        await EnrollOkAsync(student, IdOf(await GroupAsync("Futbol", 20, null, Slot("Monday", "17:00", "18:00"))), "2027-01-04");
        await EnrollOkAsync(student, IdOf(await GroupAsync("Yüzme", 20, null, Slot("Tuesday", "17:00", "18:00"))), "2027-01-05");

        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = "2027-01-12" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StudentEnrollmentsAsync(student)).EnumerateArray()
            .ShouldAllBe(e => e.GetProperty("status").GetString() == "Ended" && e.GetProperty("endDate").GetString() == "2027-01-12");
    }

    [Fact]
    [Trait("Story", "US-15")]
    public async Task Leave_date_before_an_active_enrollments_start_is_rejected()
    {
        var student = await RegisterStudentAsync();
        await EnrollOkAsync(student, IdOf(await GroupAsync("Futbol")), "2027-01-10");

        var response = await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = "2027-01-09" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Student.InvalidLeaveDate");
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct)).GetProperty("status").GetString().ShouldBe("Active");
        (await StudentEnrollmentsAsync(student))[0].GetProperty("status").GetString().ShouldBe("Active");
    }

    [Fact]
    [Trait("Story", "US-11")]
    public async Task Archiving_a_group_ends_its_enrollments_on_the_archive_date()
    {
        var group = await GroupAsync("U10 Futbol");
        var student = await RegisterStudentAsync();
        await EnrollOkAsync(student, IdOf(group), "2027-01-04");

        (await ArchiveAsync(IdOf(group))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var enrollment = (await StudentEnrollmentsAsync(student))[0];
        enrollment.GetProperty("status").GetString().ShouldBe("Ended");
        enrollment.GetProperty("endDate").GetString().ShouldBe(Today);
    }

    [Fact]
    [Trait("Story", "US-11")]
    public async Task Capacity_cannot_drop_below_the_active_enrollments()
    {
        var group = await GroupAsync("U10 Futbol", capacity: 5);
        await EnrollOkAsync(await RegisterStudentAsync("Ali"), IdOf(group));
        await EnrollOkAsync(await RegisterStudentAsync("Veli"), IdOf(group));
        group = await GetGroupAsync(IdOf(group));

        object Edit(int capacity) => new
        {
            name = "U10 Futbol",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity,
            facilityId = FieldA,
            version = VersionOf(group),
        };

        var below = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(1), Ct);
        var equal = await Admin.PutAsJsonAsync($"/api/v1/groups/{IdOf(group)}", Edit(2), Ct);

        below.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await BodyOf(below)).GetProperty("args").GetProperty("count").GetInt32().ShouldBe(2);
        equal.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    [Trait("Story", "US-16")]
    public async Task Student_search_filters_by_active_enrollments_in_a_group_or_sport()
    {
        var swimming = await CreateSportAsync(Admin, "Yüzme");
        var football = await GroupAsync("U10 Futbol");
        var swimResponse = await PostGroupAsync(NewGroup("Yüzme Başlangıç", sportId: swimming));
        var swim = await BodyOf(swimResponse);
        var ali = await RegisterStudentAsync("Ali");
        var veli = await RegisterStudentAsync("Veli");
        await RegisterStudentAsync("Can");
        await EnrollOkAsync(ali, IdOf(football));
        var ended = await EnrollOkAsync(veli, IdOf(football));
        await EndAsync(IdOf(ended), Today);
        await EnrollOkAsync(veli, IdOf(swim));

        async Task<string?[]> Names(string query) =>
            [.. (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students?{query}", Ct)).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("firstName").GetString())];

        (await Names($"groupId={IdOf(football)}")).ShouldBe(["Ali"]);
        (await Names($"sportId={Football}")).ShouldBe(["Ali"]);
        (await Names($"sportId={swimming}")).ShouldBe(["Veli"]);
        (await Names($"sportId={swimming}&groupId={IdOf(football)}")).ShouldBeEmpty();
    }
}

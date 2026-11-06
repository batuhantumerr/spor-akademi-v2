using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Students;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

[Trait("Story", "US-15")]
public sealed class LeaveStudentTests(DatabaseFixture database) : StudentsTest(database)
{
    [Fact]
    public async Task A_student_who_leaves_is_left_with_the_date_and_hidden_from_active_lists()
    {
        var student = await RegisterAsync(Minor());

        var response = await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-10" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var left = (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
        left.Status.ShouldBe(StudentStatus.Left);
        left.LeftAt.ShouldBe(new DateOnly(2027, 1, 10));
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students?status=Active", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students?status=Left", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Leaving_ends_the_active_enrollments_on_the_leave_date()
    {
        var recorder = new RecordingEnrollmentEnder();
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IEnrollmentEnder>(_ => recorder))));
        using var admin = api.CreateClient();
        admin.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));
        var student = await RegisterAsync(Minor());

        (await admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-10" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        recorder.Calls.ShouldBe([(StudentId.From(student.Id), new DateOnly(2027, 1, 10))]);
    }

    [Fact]
    public async Task Leaving_twice_is_rejected()
    {
        var student = await RegisterAsync(Minor());
        await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-10" }, Ct);

        var again = await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-12" }, Ct);

        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(again)).ShouldBe("Student.AlreadyLeft");
        (await GetAsync(student.Id))!.LeftAt.ShouldBe(new DateOnly(2027, 1, 10));
    }

    [Theory]
    [InlineData("2027-01-15")] // tomorrow in the school's time zone
    [InlineData("2015-04-30")] // before the birth date
    public async Task The_leave_date_cannot_be_in_the_future_or_before_birth(string date)
    {
        var student = await RegisterAsync(Minor());

        var response = await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).GetProperty("date")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task Today_is_a_valid_leave_date_and_the_date_is_required()
    {
        var student = await RegisterAsync(Minor());

        var missing = await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { }, Ct);
        var today = await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-14" }, Ct);

        (await ErrorsOf(missing)).GetProperty("date")[0].GetString().ShouldBe("Field.Required");
        today.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_student_who_left_can_be_reactivated()
    {
        var student = await RegisterAsync(Minor());
        await Admin.PostAsJsonAsync($"/api/v1/students/{student.Id}/leave", new { date = "2027-01-10" }, Ct);

        var response = await Admin.PostAsync(Url($"/api/v1/students/{student.Id}/reactivate"), null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var active = (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
        active.Status.ShouldBe(StudentStatus.Active);
        active.LeftAt.ShouldBeNull();
    }

    [Fact]
    public async Task Only_a_student_who_left_can_be_reactivated()
    {
        var student = await RegisterAsync(Minor());

        var response = await Admin.PostAsync(Url($"/api/v1/students/{student.Id}/reactivate"), null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Student.NotLeft");
    }

    [Fact]
    public async Task An_unknown_student_is_not_found()
    {
        var id = Guid.CreateVersion7();

        (await Admin.PostAsJsonAsync($"/api/v1/students/{id}/leave", new { date = "2027-01-10" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Admin.PostAsync(Url($"/api/v1/students/{id}/reactivate"), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed class RecordingEnrollmentEnder : IEnrollmentEnder
    {
        private readonly ConcurrentQueue<(StudentId, DateOnly)> _calls = new();

        public IReadOnlyList<(StudentId, DateOnly)> Calls => [.. _calls];

        public Task EndActiveEnrollmentsOfStudentAsync(StudentId studentId, DateOnly endDate, CancellationToken cancellationToken)
        {
            _calls.Enqueue((studentId, endDate));
            return Task.CompletedTask;
        }
    }
}

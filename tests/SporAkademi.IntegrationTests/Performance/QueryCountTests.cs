using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;
using SporAkademi.IntegrationTests.Parents;

namespace SporAkademi.IntegrationTests.Performance;

/// <summary>
/// P4-08 (NFR-PRF-06, DEC-566): the endpoints of the load test's mix run a fixed number of SQL commands, whatever the
/// size of the school: the count with eight students in the group equals the count with two (no N+1), and stays within
/// a budget (a regression shows up as a changed count, with the commands in the failure message). The connection
/// openings are counted too: each one also costs the row level security round trip (DEC-524).
/// </summary>
public sealed class QueryCountTests(DatabaseFixture database) : ParentTest(database)
{
    private const string SessionKey = "2027-01-12T17:00"; // last Tuesday, within the coach's 48 hours
    private const string ParentEmail = "sayim-velisi@example.com";

    private Guid _group;
    private HttpClient _coach = null!;
    private HttpClient? _parent;
    private Guid _child;
    private int _students;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var group = await GroupAsync("U10 Futbol", 30, Ayse, Slot("Tuesday", "17:00", "18:15"), Slot("Thursday", "17:00", "18:15"));
        _group = IdOf(group);
        await DefineFeeOkAsync(_group, "2027-01", "1000.00");
        _coach = CoachClient(Ayse);
    }

    public override async ValueTask DisposeAsync()
    {
        _coach.Dispose();
        _parent?.Dispose();
        await base.DisposeAsync();
    }

    public static TheoryData<string, int, int> Endpoints => new()
    {
        // endpoint, command budget, connection opening budget
        { "GET /dashboard", 11, 11 },
        { "GET /students", 2, 2 },
        { "GET /students/{id}", 4, 4 },
        { "GET /dues/outstanding", 6, 6 },
        { "GET /payments", 4, 4 },
        { "GET /groups", 5, 5 },
        { "GET /groups/{id}/roster", 3, 3 },
        { "GET /groups/{id}/attendance", 4, 4 },
        { "GET /reports/attendance", 5, 5 },
        { "GET /reports/occupancy", 5, 5 },
        { "GET /reports/revenue", 5, 5 },
        { "GET /me/sessions (coach)", 2, 2 },
        { "GET /groups/{id}/sessions/{key}/attendance (coach)", 4, 4 },
        { "PUT /groups/{id}/sessions/{key}/attendance (coach)", 10, 5 },
        { "POST /payments", 12, 4 },
        { "GET /parent/children (parent)", 1, 1 },
        { "GET /parent/children/{id}/dues (parent)", 7, 7 },
        { "GET /parent/children/{id}/attendance (parent)", 5, 5 },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    [Trait("Story", "P4-08")]
    public async Task The_query_count_does_not_grow_with_the_school(string endpoint, int commandBudget, int openingBudget)
    {
        await GrowAsync(2);
        var call = await PrepareAsync(endpoint);
        await WarmAsync(endpoint, call);
        var small = await Api.Queries.MeasureAsync(() => SucceedsAsync(endpoint, call));

        await GrowAsync(6);
        call = await PrepareAsync(endpoint);
        await WarmAsync(endpoint, call);
        var large = await Api.Queries.MeasureAsync(() => SucceedsAsync(endpoint, call));

        TestContext.Current.TestOutputHelper?.WriteLine($"{endpoint}: {large.Commands.Count} commands, {large.Openings} openings");
        large.Commands.Count.ShouldBe(small.Commands.Count, $"{endpoint} with 8 students:{Environment.NewLine}{large}{Environment.NewLine}with 2:{Environment.NewLine}{small}");
        large.Commands.Count.ShouldBeLessThanOrEqualTo(commandBudget, large.ToString());
        large.Openings.ShouldBeLessThanOrEqualTo(openingBudget, large.ToString());
    }

    /// <summary>
    /// Enrolls <paramref name="count"/> more students, each with a payment, and records last Tuesday's attendance. Every
    /// second one goes into a new group of its own (another coach, no schedule), so the group count grows too.
    /// </summary>
    private async Task GrowAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Guid student;
            if (_parent is null)
            {
                // The first child belongs to a parent with an account, for the parent endpoints.
                (student, var guardian) = await RegisterChildAsync("Sayım", ParentEmail);
                _child = student;
                await EnrollOkAsync(student, _group, "2027-01-04");
                (_parent, _) = await SignedInParentAsync(guardian, ParentEmail);
            }
            else
            {
                student = await RegisterStudentAsync($"Öğrenci{++_students}", "Sayım");
                var group = _group;
                if (_students % 2 == 0)
                {
                    group = IdOf(await GroupAsync($"Ek Grup {_students}", 10, Mehmet));
                    await DefineFeeOkAsync(group, "2027-01", "800.00");
                }

                await EnrollOkAsync(student, group, "2027-01-04");
            }

            var enrollment = (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}/enrollments", Ct))[0];
            await RecordOkAsync(IdOf(enrollment), "2027-01", "100.00");
        }

        var records = await MarksAsync("Present");
        (await _coach.PutAsJsonAsync($"/api/v1/groups/{_group}/sessions/{SessionKey}/attendance", new { records }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The request to measure; what a write needs first (the attendance list, the enrollment) is read here, unmeasured.</summary>
    private async Task<Func<Task<HttpResponseMessage>>> PrepareAsync(string endpoint)
    {

        switch (endpoint)
        {
            case "PUT /groups/{id}/sessions/{key}/attendance (coach)":
                var records = await MarksAsync("Late");
                return () => _coach.PutAsJsonAsync($"/api/v1/groups/{_group}/sessions/{SessionKey}/attendance", new { records }, Ct);
            case "POST /payments":
                var enrollment = IdOf((await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{_child}/enrollments", Ct))[0]);
                return () => RecordAsync(enrollment, "2027-01", "1.00");
        }

        var (client, path) = endpoint switch
        {
            "GET /dashboard" => (Admin, "/api/v1/dashboard"),
            "GET /students" => (Admin, "/api/v1/students?pageSize=20"),
            "GET /students/{id}" => (Admin, $"/api/v1/students/{_child}"),
            "GET /dues/outstanding" => (Admin, "/api/v1/dues/outstanding?period=2027-01"),
            "GET /payments" => (Admin, "/api/v1/payments?pageSize=20"),
            "GET /groups" => (Admin, "/api/v1/groups?pageSize=50"),
            "GET /groups/{id}/roster" => (Admin, $"/api/v1/groups/{_group}/roster"),
            "GET /groups/{id}/attendance" => (Admin, $"/api/v1/groups/{_group}/attendance?month=2027-01"),
            "GET /reports/attendance" => (Admin, "/api/v1/reports/attendance"),
            "GET /reports/occupancy" => (Admin, "/api/v1/reports/occupancy"),
            "GET /reports/revenue" => (Admin, "/api/v1/reports/revenue"),
            "GET /me/sessions (coach)" => (_coach, "/api/v1/me/sessions?date=2027-01-12"),
            "GET /groups/{id}/sessions/{key}/attendance (coach)" => (_coach, $"/api/v1/groups/{_group}/sessions/{SessionKey}/attendance"),
            "GET /parent/children (parent)" => (_parent!, "/api/v1/parent/children"),
            "GET /parent/children/{id}/dues (parent)" => (_parent!, $"/api/v1/parent/children/{_child}/dues"),
            "GET /parent/children/{id}/attendance (parent)" => (_parent!, $"/api/v1/parent/children/{_child}/attendance"),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null),
        };
        return () => client.GetAsync(Url(path), Ct);
    }

    /// <summary>A status for every student on the session's attendance list.</summary>
    private async Task<List<object>> MarksAsync(string status)
    {
        var sheet = await _coach.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{_group}/sessions/{SessionKey}/attendance", Ct);
        return [.. sheet.GetProperty("students").EnumerateArray().Select(s => (object)new { studentId = s.GetProperty("studentId").GetGuid(), status })];
    }

    /// <summary>
    /// The same request once, unmeasured, so both measurements find the small caches (school time zone, feature flags,
    /// sports and facilities) as almost every request does. Not for the dashboard and the reports: they are cached as a
    /// whole (DEC-311, DEC-410), so their cost is the miss; the growth's writes invalidate them before each measurement
    /// (DEC-313), and only the feature flags they are gated by (P4-10) are warmed.
    /// </summary>
    private async Task WarmAsync(string endpoint, Func<Task<HttpResponseMessage>> call)
    {
        if (endpoint == "GET /dashboard" || endpoint.StartsWith("GET /reports/", StringComparison.Ordinal))
        {
            (await Admin.GetAsync(Url("/api/v1/features"), Ct)).EnsureSuccessStatusCode();
        }
        else
        {
            await SucceedsAsync(endpoint, call);
        }
    }

    private static async Task SucceedsAsync(string endpoint, Func<Task<HttpResponseMessage>> call)
    {
        using var response = await call();
        response.IsSuccessStatusCode.ShouldBeTrue($"{endpoint}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);
}

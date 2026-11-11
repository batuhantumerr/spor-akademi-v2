using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>
/// P3-01 (DEC-377, DEC-382): the parent boundary, the second authorization boundary after the school (P1-35). A signed-in
/// parent calls every parent endpoint with another family's ids, of the same school and of another school, and must get
/// the owning module's "not found"; nothing may change; the parent's own lists may hold no other family's data; and
/// every School Admin or coach endpoint refuses a parent (403). Like the school suite, the endpoints come from the
/// running API and guard tests fail when a parent endpoint is added without a probe.
/// </summary>
[Trait("Story", "P3-01")]
public sealed partial class ParentIsolationTests(DatabaseFixture database) : ParentTest(database)
{
    private const string OwnEmail = "kendi.veli@example.com";
    private const string SessionKey = "2027-01-14T10:00";

    private Family _own = null!;
    private Family _neighbour = null!;
    private Family _otherSchool = null!;
    private HttpClient _parent = null!;
    private HttpClient _adminB = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        // One group both families of school A train in, so a leak through the group (roster, attendance, dues) would show.
        var group = IdOf(await GroupAsync("U10 Futbol", 20, Ayse, Slot("Thursday", "10:00", "11:00")));
        await DefineFeeOkAsync(group, "2027-01", "1000.00");
        _own = await FamilyAsync(Admin, group, "Kendi", OwnEmail);
        _neighbour = await FamilyAsync(Admin, group, "Komşu", "komsu.veli@example.com");
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{group}/sessions/{SessionKey}/attendance", new
        {
            records = new[] { new { studentId = _own.Student, status = "Present" }, new { studentId = _neighbour.Student, status = "Late" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Admin.PostAsJsonAsync($"/api/v1/groups/{group}/announcements", new { title = "Ortak duyuru", body = "Herkese." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        _adminB = Api.SchoolAdminClient(schoolB);
        var sport = await CreateSportAsync(_adminB, "Yüzme");
        var facility = await CreateFacilityAsync(_adminB, "B Havuzu");
        var groupB = await PostGroupAsync(NewGroup("B Grubu", null, facility, sport), _adminB);
        groupB.StatusCode.ShouldBe(HttpStatusCode.Created);
        var groupBId = IdOf(await BodyOf(groupB));
        await DefineFeeOkAsync(groupBId, "2027-01", "800.00", _adminB);
        _otherSchool = await FamilyAsync(_adminB, groupBId, "Uzak", "uzak.veli@example.com");

        _parent = (await SignedInParentAsync(_own.Guardian, OwnEmail)).Client;
    }

    public override async ValueTask DisposeAsync()
    {
        _parent.Dispose();
        _adminB.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Another_familys_ids_are_not_found_on_every_parent_endpoint_and_nothing_changes()
    {
        var before = await OnlinePaymentCountAsync();
        var failures = new List<string>();

        foreach (var foreign in new[] { _neighbour, _otherSchool })
        {
            foreach (var endpoint in ParentEndpoints().Where(e => e.Parameters.Count > 0))
            {
                var (url, code) = Probe(endpoint, foreign);
                failures.AddRange(await ExpectAsync(endpoint.Method, url, null, 404, code));
            }

            // The one write: starting an online payment for another family's due.
            failures.AddRange(await ExpectAsync(
                "POST", "/api/v1/parent/online-payments", new { enrollmentId = foreign.Enrollment, period = "2027-01" }, 404, "Enrollment.NotFound", "sizma-veli-0001"));
        }

        failures.ShouldBeEmpty();
        (await OnlinePaymentCountAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Control_the_same_reads_succeed_for_the_parents_own_child()
    {
        // Without this, a wrong URL in a probe would also answer 404 and the suite would pass for nothing.
        var failures = new List<string>();
        foreach (var endpoint in ParentEndpoints().Where(e => e.Method == "GET" && e.Parameters.Count > 0))
        {
            var (url, _) = Probe(endpoint, _own);
            failures.AddRange(await ExpectAsync("GET", url, null, 200, null));
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_parents_own_lists_hold_no_other_familys_data()
    {
        var reads = new List<string> { "/api/v1/parent/children" };
        reads.AddRange(ParentEndpoints().Where(e => e.Method == "GET" && e.Route.StartsWith("/api/v1/parent/children/", StringComparison.Ordinal))
            .Select(e => Probe(e, _own).Url));
        reads.Add($"/api/v1/parent/online-payments/{_own.OnlinePayment}");
        var leaks = new List<string>();

        foreach (var url in reads)
        {
            var body = await (await _parent.GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);
            leaks.AddRange(_neighbour.Markers.Concat(_otherSchool.Markers).Concat(_own.PrivateMarkers)
                .Where(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                .Select(marker => $"GET {url} contains {marker}"));
        }

        leaks.ShouldBeEmpty();
        IdsIn(await GetOkAsync(_parent, "/api/v1/parent/children")).ShouldBe([_own.Student]);
    }

    [Fact]
    public async Task Every_school_admin_and_coach_endpoint_refuses_a_parent()
    {
        var failures = new List<string>();
        foreach (var endpoint in Endpoints().Where(e => !e.IsAnonymous && e.Roles.Count > 0 && !e.Roles.Contains("Parent")))
        {
            var url = RouteParameter().Replace(endpoint.Route, match => match.Groups["name"].Value switch
            {
                "sessionKey" => SessionKey,
                "effectiveFrom" => "2027-06",
                _ => _neighbour.Student.ToString(),
            });
            failures.AddRange(await ExpectAsync(endpoint.Method, url, null, 403, null));
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Every_parent_endpoint_is_for_parents_only_and_probed()
    {
        var parent = ParentEndpoints();

        parent.ShouldNotBeEmpty();
        parent.Where(e => e.IsAnonymous || !e.Roles.SequenceEqual(["Parent"])).Select(e => e.Key).ShouldBeEmpty();
        // The parent role reaches nothing outside /api/v1/parent (besides the any-role /me and change-password).
        Endpoints().Where(e => e.Roles.Contains("Parent") && !e.Route.StartsWith("/api/v1/parent/", StringComparison.Ordinal))
            .Select(e => e.Key).ShouldBeEmpty();
        parent.Select(e => e.Key).ShouldBe(
        [
            "GET /api/v1/parent/children",
            "GET /api/v1/parent/children/{id:guid}/schedule",
            "GET /api/v1/parent/children/{id:guid}/attendance",
            "GET /api/v1/parent/children/{id:guid}/dues",
            "GET /api/v1/parent/children/{id:guid}/payments",
            "GET /api/v1/parent/children/{id:guid}/announcements",
            "POST /api/v1/parent/online-payments",
            "GET /api/v1/parent/online-payments/{id:guid}",
        ], ignoreOrder: true, "A new parent endpoint needs a probe in this suite (DEC-382).");
    }

    /// <summary>The URL with <paramref name="family"/>'s id and the "not found" its module answers.</summary>
    private static (string Url, string Code) Probe(ApiEndpoint endpoint, Family family)
    {
        var onlinePayment = endpoint.Route.StartsWith("/api/v1/parent/online-payments/", StringComparison.Ordinal);
        var id = onlinePayment ? family.OnlinePayment : family.Student;
        var url = RouteParameter().Replace(endpoint.Route, match => match.Groups["name"].Value == "id"
            ? id.ToString()
            : throw new InvalidOperationException($"{endpoint.Key}: no value for '{match.Groups["name"].Value}'."));
        return (url, onlinePayment ? "OnlinePayment.NotFound" : "Student.NotFound");
    }

    private async Task<IEnumerable<string>> ExpectAsync(string method, string url, object? body, int status, string? code, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        var response = await _parent.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        string? actual = null;
        if (text.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(text);
            actual = json.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        }

        return (int)response.StatusCode == status && (code is null || actual == code)
            ? []
            : [$"{method} {url}: expected {status} {code}, got {(int)response.StatusCode} {actual}"];
    }

    private async Task<long> OnlinePaymentCountAsync() =>
        (long)(await ScalarAsync("SELECT count(*) FROM billing.online_payments WHERE id <> @id", Guid.Empty))!;

    /// <summary>A family: a child with a guardian, enrolled in the group, a payment, an online payment attempt.</summary>
    private async Task<Family> FamilyAsync(HttpClient admin, Guid group, string name, string email)
    {
        var (student, guardian) = await RegisterChildAsync(name, email, lastName: name, client: admin);
        var enrolled = await EnrollAsync(student, group, "2027-01-04", admin);
        enrolled.StatusCode.ShouldBe(HttpStatusCode.Created);
        var enrollment = IdOf(await BodyOf(enrolled));
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "100.00", admin));
        var online = IdOf(await StartOnlineOkAsync(enrollment, client: admin));
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var phone = detail.GetProperty("guardians")[0].GetProperty("phone").GetString()!;
        var emergency = detail.GetProperty("emergencyContact").GetProperty("phone").GetString()!;
        return new Family(student, guardian, enrollment, payment, online, name, email, phone, emergency);
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(:[^}]*)?\}")]
    private static partial Regex RouteParameter();

    private List<ApiEndpoint> ParentEndpoints() => [.. Endpoints().Where(e => e.Route.StartsWith("/api/v1/parent/", StringComparison.Ordinal))];

    private List<ApiEndpoint> Endpoints() =>
    [
        .. Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => new ApiEndpoint(
                method,
                e.RoutePattern.RawText!.TrimEnd('/'),
                [.. e.RoutePattern.Parameters.Select(p => p.Name)],
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                [.. e.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
                    .SelectMany(p => p.Requirements.OfType<RolesAuthorizationRequirement>())
                    .SelectMany(r => r.AllowedRoles)
                    .Distinct()]))),
    ];

    private sealed record ApiEndpoint(string Method, string Route, IReadOnlyList<string> Parameters, bool IsAnonymous, IReadOnlyList<string> Roles)
    {
        public string Key => $"{Method} {Route}";
    }

    private sealed record Family(
        Guid Student,
        Guid Guardian,
        Guid Enrollment,
        Guid Payment,
        Guid OnlinePayment,
        string Name,
        string Email,
        string GuardianPhone,
        string EmergencyPhone)
    {
        /// <summary>What must never reach another family: ids, the child's name and the guardian's contact data.</summary>
        public IEnumerable<string> Markers =>
        [
            .. new[] { Student, Guardian, Enrollment, Payment, OnlinePayment }.Select(id => id.ToString()),
            Name,
            Email,
            GuardianPhone.TrimStart('+'),
        ];

        /// <summary>What even the family's own parent views leave out (DEC-380): the emergency contact and the guardian ids.</summary>
        public IEnumerable<string> PrivateMarkers => [EmergencyPhone.TrimStart('+'), "Acil Kişi", Guardian.ToString()];
    }
}

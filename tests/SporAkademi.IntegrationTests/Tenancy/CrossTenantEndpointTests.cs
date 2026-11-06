using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

/// <summary>
/// P1-35, NFR-SEC-08, US-02: every endpoint that takes an id is called by school B with school A's ids and
/// must answer 404, leaving school A's rows untouched; every list school B can read must hold none of
/// school A's ids. The endpoints come from the running API, and <see cref="Every_parameterised_endpoint_has_a_probe"/>
/// fails when an endpoint is added without a probe here, so the suite cannot silently fall behind.
/// </summary>
[Trait("Story", "US-02")]
public sealed class CrossTenantEndpointTests(DatabaseFixture database) : BillingTest(database)
{
    private const string RecordedSessionKey = "2027-01-14T10:00";

    /// <summary>Platform Admin endpoints: school-scoped users never reach them (403 by role), reviewed by hand.</summary>
    private static readonly string[] PlatformEndpoints =
    [
        "GET /api/v1/platform/schools/{id:guid}",
        "POST /api/v1/platform/schools/{schoolId:guid}/admins/{userId:guid}/reset-password",
    ];

    private SchoolA _a = null!;
    private SchoolB _b = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _a = await SeedSchoolAAsync();
        _b = await SeedSchoolBAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        _b.Admin.Dispose();
        _b.Coach.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Another_schools_admin_gets_404_from_every_endpoint_with_an_id_and_changes_nothing()
    {
        var before = await FingerprintAsync(School);
        var failures = new List<string>();

        foreach (var endpoint in ParameterisedEndpoints().Where(e => !PlatformEndpoints.Contains(e.Key)))
        {
            failures.AddRange(await ProbeAsync(_b.Admin, endpoint));
        }

        failures.ShouldBeEmpty();
        (await FingerprintAsync(School)).ShouldBe(before);
    }

    [Fact]
    public async Task Another_schools_coach_gets_404_from_every_endpoint_with_an_id_a_coach_may_call()
    {
        var before = await FingerprintAsync(School);
        var failures = new List<string>();
        var coachEndpoints = ParameterisedEndpoints().Where(e => !PlatformEndpoints.Contains(e.Key) && e.Roles.Contains(nameof(UserRole.Coach))).ToList();

        foreach (var endpoint in coachEndpoints)
        {
            failures.AddRange(await ProbeAsync(_b.Coach, endpoint));
        }

        coachEndpoints.ShouldNotBeEmpty();
        failures.ShouldBeEmpty();
        (await FingerprintAsync(School)).ShouldBe(before);
    }

    [Fact]
    public async Task Lists_and_searches_of_another_school_hold_none_of_this_schools_ids()
    {
        var leaks = new List<string>();
        foreach (var endpoint in CollectionEndpoints())
        {
            var clients = new List<(string Name, HttpClient Client)>();
            if (endpoint.Roles.Count == 0 || endpoint.Roles.Contains(nameof(UserRole.SchoolAdmin)))
            {
                clients.Add(("admin", _b.Admin));
            }

            if (endpoint.Roles.Count == 0 || endpoint.Roles.Contains(nameof(UserRole.Coach)))
            {
                clients.Add(("coach", _b.Coach));
            }

            foreach (var (name, client) in clients)
            {
                var url = endpoint.Route + ListQueries.GetValueOrDefault(endpoint.Route, string.Empty);
                var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    leaks.Add($"{name} GET {url}: {(int)response.StatusCode} {body}");
                    continue;
                }

                leaks.AddRange(_a.Ids.Where(id => body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)).Select(id => $"{name} GET {url} contains {id}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Control_the_same_reads_succeed_for_the_owning_school()
    {
        // Without this, a wrong URL or id in a probe would also answer 404 and the suite would pass for nothing.
        var failures = new List<string>();
        foreach (var endpoint in ParameterisedEndpoints().Where(e => e.Method == "GET" && !PlatformEndpoints.Contains(e.Key)))
        {
            var url = endpoint.Route
                .Replace("{id:guid}", ResourceId(endpoint.Route).ToString(), StringComparison.Ordinal)
                .Replace("{sessionKey}", RecordedSessionKey, StringComparison.Ordinal) + Probes(_a, _b)[endpoint.Key].Query;
            var response = await Admin.GetAsync(new Uri(url, UriKind.Relative), Ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"GET {url}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Every_parameterised_endpoint_has_a_probe()
    {
        var covered = Probes(_a, _b).Keys.Concat(PlatformEndpoints).ToHashSet(StringComparer.Ordinal);
        var actual = ParameterisedEndpoints().Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        actual.Except(covered).ShouldBeEmpty("Add a probe to CrossTenantEndpointTests for each new endpoint with a route parameter.");
        covered.Except(actual).ShouldBeEmpty("Remove probes of endpoints that no longer exist.");
    }

    [Fact]
    public void Every_collection_endpoint_is_checked_for_leaks()
    {
        // Parameterless GETs school users can call: each one is read by the leak test above.
        CollectionEndpoints().Select(e => e.Route).ShouldBe(
        [
            "/api/v1/coaches",
            "/api/v1/dues/outstanding",
            "/api/v1/facilities",
            "/api/v1/groups",
            "/api/v1/guardians",
            "/api/v1/me",
            "/api/v1/me/announcements",
            "/api/v1/me/groups",
            "/api/v1/me/sessions",
            "/api/v1/payments",
            "/api/v1/sports",
            "/api/v1/students",
        ], ignoreOrder: true);
    }

    /// <summary>Calls the endpoint with school A's ids; anything but 404 is a failure.</summary>
    private async Task<IEnumerable<string>> ProbeAsync(HttpClient client, ApiEndpoint endpoint)
    {
        var probe = Probes(_a, _b)[endpoint.Key];
        var url = endpoint.Route
            .Replace("{id:guid}", ResourceId(endpoint.Route).ToString(), StringComparison.Ordinal)
            .Replace("{sessionKey}", RecordedSessionKey, StringComparison.Ordinal) + probe.Query;
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), new Uri(url, UriKind.Relative));
        if (probe.Body is not null)
        {
            request.Content = JsonContent.Create(probe.Body);
        }

        var response = await client.SendAsync(request, Ct);
        return response.StatusCode == HttpStatusCode.NotFound
            ? []
            : [$"{endpoint.Method} {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}"];
    }

    /// <summary>School A's id for the resource the route names (the first segment after /api/v1).</summary>
    private Guid ResourceId(string route) => route.Split('/', StringSplitOptions.RemoveEmptyEntries)[2] switch
    {
        "sports" => _a.Sport,
        "facilities" => _a.Facility,
        "coaches" => _a.Coach,
        "groups" => _a.Group,
        "students" => _a.Student,
        "guardians" => _a.Guardian,
        "enrollments" => _a.Enrollment,
        "payments" => _a.Payment,
        var other => throw new InvalidOperationException($"No school A resource for '{other}' ({route})."),
    };

    /// <summary>
    /// A request per endpoint that passes validation, so only the foreign id can stop it. Bodies refer to
    /// school B's own sport, facility and coach; versions are plausible but irrelevant (the row is not found).
    /// </summary>
    private static Dictionary<string, Probe> Probes(SchoolA a, SchoolB b) => new(StringComparer.Ordinal)
    {
        ["GET /api/v1/sports/{id:guid}"] = new(),
        ["PUT /api/v1/sports/{id:guid}"] = new(new { name = "Hack", version = 1 }),
        ["POST /api/v1/sports/{id:guid}/archive"] = new(),
        ["DELETE /api/v1/sports/{id:guid}"] = new(),
        ["GET /api/v1/facilities/{id:guid}"] = new(),
        ["PUT /api/v1/facilities/{id:guid}"] = new(new { name = "Hack", type = "Field", address = Address, capacity = 10, version = 1 }),
        ["POST /api/v1/facilities/{id:guid}/archive"] = new(),
        ["DELETE /api/v1/facilities/{id:guid}"] = new(),
        ["GET /api/v1/coaches/{id:guid}"] = new(),
        ["PUT /api/v1/coaches/{id:guid}"] = new(new { firstName = "Hack", lastName = "Deneme", phone = (string?)null, version = 1 }),
        ["POST /api/v1/coaches/{id:guid}/deactivate"] = new(),
        ["POST /api/v1/coaches/{id:guid}/reset-password"] = new(),
        ["GET /api/v1/groups/{id:guid}"] = new(),
        ["PUT /api/v1/groups/{id:guid}"] = new(new
        {
            name = "Hack",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity = 20,
            facilityId = b.Facility,
            coachId = (Guid?)null,
            version = 1,
        }),
        ["PUT /api/v1/groups/{id:guid}/schedule"] = new(new { slots = new[] { new { day = "Monday", start = "08:00", end = "09:00" } }, version = 1 }),
        ["POST /api/v1/groups/{id:guid}/archive"] = new(),
        ["GET /api/v1/groups/{id:guid}/sessions"] = new(Query: "?from=2027-01-01&to=2027-01-31"),
        ["GET /api/v1/groups/{id:guid}/fees"] = new(),
        ["POST /api/v1/groups/{id:guid}/fees"] = new(new { effectiveFrom = "2027-02", amount = new { amount = "1.00", currency = "TRY" } }),
        ["GET /api/v1/groups/{id:guid}/enrollments"] = new(),
        ["GET /api/v1/groups/{id:guid}/roster"] = new(),
        ["GET /api/v1/groups/{id:guid}/announcements"] = new(),
        ["POST /api/v1/groups/{id:guid}/announcements"] = new(new { title = "Hack", body = "Hack" }),
        ["GET /api/v1/groups/{id:guid}/attendance"] = new(Query: "?month=2027-01"),
        ["GET /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance"] = new(),
        ["PUT /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance"] = new(new { records = new[] { new { studentId = a.Student, status = "Absent" } } }),
        ["GET /api/v1/students/{id:guid}"] = new(),
        ["PUT /api/v1/students/{id:guid}"] = new(new
        {
            firstName = "Hack",
            lastName = "Deneme",
            birthDate = "1990-01-01",
            gender = "Male",
            phone = "0533 999 99 99",
            emergencyContact = new { name = "Acil Kişi", phone = "0533 999 99 98", relationship = "Komşu" },
            version = 1,
        }),
        ["PUT /api/v1/students/{id:guid}/guardians"] = new(new { guardians = Array.Empty<object>(), version = 1 }),
        ["POST /api/v1/students/{id:guid}/leave"] = new(new { date = "2027-01-14" }),
        ["POST /api/v1/students/{id:guid}/reactivate"] = new(),
        ["GET /api/v1/students/{id:guid}/enrollments"] = new(),
        ["GET /api/v1/students/{id:guid}/attendance"] = new(Query: "?from=2027-01-01&to=2027-01-31"),
        ["PUT /api/v1/guardians/{id:guid}"] = new(new { firstName = "Hack", lastName = "Deneme", phone = "0533 999 99 97", version = 1 }),
        ["GET /api/v1/enrollments/{id:guid}"] = new(),
        ["POST /api/v1/enrollments/{id:guid}/end"] = new(new { date = "2027-01-14" }),
        ["GET /api/v1/payments/{id:guid}"] = new(),
        ["POST /api/v1/payments/{id:guid}/cancel"] = new(new { reason = "Hack" }),
    };

    /// <summary>Query strings a list needs to answer at all; the guardian search uses school A's real phone.</summary>
    private Dictionary<string, string> ListQueries => new(StringComparer.Ordinal)
    {
        ["/api/v1/guardians"] = "?phone=" + Uri.EscapeDataString(_a.GuardianPhone),
        ["/api/v1/students"] = "?search=" + Uri.EscapeDataString("Kaynak"),
    };

    private static readonly object Address = new { line = "Atatürk Cad. No:1", district = "Kadıköy", city = "İstanbul" };

    private List<ApiEndpoint> Endpoints() =>
    [
        .. Api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => new ApiEndpoint(
                method,
                e.RoutePattern.RawText!.TrimEnd('/'),
                e.RoutePattern.Parameters.Count > 0,
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                [.. e.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
                    .SelectMany(p => p.Requirements.OfType<RolesAuthorizationRequirement>())
                    .SelectMany(r => r.AllowedRoles)
                    .Distinct()]))),
    ];

    private List<ApiEndpoint> ParameterisedEndpoints() => [.. Endpoints().Where(e => e.HasParameters)];

    /// <summary>Parameterless GETs a school user can call (not anonymous, not Platform Admin only).</summary>
    private List<ApiEndpoint> CollectionEndpoints() =>
    [
        .. Endpoints().Where(e => e.Method == "GET" && !e.HasParameters && !e.IsAnonymous
            && (e.Roles.Count == 0 || e.Roles.Contains(nameof(UserRole.SchoolAdmin)) || e.Roles.Contains(nameof(UserRole.Coach)))),
    ];

    /// <summary>
    /// A fingerprint of every row school A owns: each table with a <c>tenant_id</c> column plus the school row,
    /// read directly as the migrator (no tenant filter).
    /// </summary>
    private async Task<string> FingerprintAsync(SchoolId school)
    {
        await using var connection = new NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT table_schema || '.' || table_name FROM information_schema.columns WHERE column_name = 'tenant_id' AND table_schema NOT IN ('public', 'pg_catalog', 'information_schema') ORDER BY 1",
            connection))
        await using (var reader = await list.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Count.ShouldBeGreaterThan(10);
        var fingerprint = new StringBuilder();
        foreach (var table in tables.Append("tenancy.schools"))
        {
            var column = table == "tenancy.schools" ? "id" : "tenant_id";
            // Table names come from the catalog above, not from input.
#pragma warning disable CA2100
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t WHERE {column} = @school",
                connection);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("school", school.Value);
            fingerprint.Append(table).Append('=').Append(await command.ExecuteScalarAsync(Ct)).Append('\n');
        }

        return fingerprint.ToString();
    }

    /// <summary>One of every resource in school A, created over HTTP like real use.</summary>
    private async Task<SchoolA> SeedSchoolAAsync()
    {
        var group = IdOf(await GroupAsync("U10 Futbol", 20, Ayse, Slot("Thursday", "10:00", "11:00")));
        await DefineFeeOkAsync(group, "2027-01", "1000.00");
        var student = await RegisterStudentAsync("Kaynak", "Okulu");
        var studentDto = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var guardian = studentDto.GetProperty("guardians")[0];
        var enrollment = IdOf(await EnrollOkAsync(student, group, "2027-01-04"));
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "1000.00"));
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{group}/sessions/{RecordedSessionKey}/attendance", new { records = new[] { new { studentId = student, status = "Present" } } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var announcement = await Admin.PostAsJsonAsync($"/api/v1/groups/{group}/announcements", new { title = "Duyuru", body = "Antrenman saati değişti." }, Ct);
        announcement.StatusCode.ShouldBe(HttpStatusCode.Created);

        return new SchoolA(
            Football,
            FieldA,
            Ayse.Id.Value,
            group,
            student,
            guardian.GetProperty("guardianId").GetGuid(),
            guardian.GetProperty("phone").GetString()!,
            enrollment,
            payment,
            IdOf(await BodyOf(announcement)));
    }

    /// <summary>School B: an admin, a coach, and a sport and facility its request bodies can refer to.</summary>
    private async Task<SchoolB> SeedSchoolBAsync()
    {
        var school = await TestSchools.CreateAsync(Api, "okul-b");
        var adminUser = await SeedUserAsync("yonetici@okul-b.test", UserRole.SchoolAdmin, school);
        var admin = Api.CreateClient();
        admin.Authorize(TestTokens.Create(adminUser.Id.Value, nameof(UserRole.SchoolAdmin), school.Value, Api.Clock.GetUtcNow()));
        var coachUser = await SeedCoachAsync(school);
        return new SchoolB(admin, CoachClient(coachUser), await CreateSportAsync(admin, "Yüzme"), await CreateFacilityAsync(admin, "B Havuzu"));
    }

    private sealed record ApiEndpoint(string Method, string Route, bool HasParameters, bool IsAnonymous, IReadOnlyCollection<string> Roles)
    {
        public string Key => $"{Method} {Route}";
    }

    private sealed record Probe(object? Body = null, string Query = "");

    private sealed record SchoolA(Guid Sport, Guid Facility, Guid Coach, Guid Group, Guid Student, Guid Guardian, string GuardianPhone, Guid Enrollment, Guid Payment, Guid Announcement)
    {
        public Guid[] Ids => [Sport, Facility, Coach, Group, Student, Guardian, Enrollment, Payment, Announcement];
    }

    private sealed record SchoolB(HttpClient Admin, HttpClient Coach, Guid Sport, Guid Facility);
}

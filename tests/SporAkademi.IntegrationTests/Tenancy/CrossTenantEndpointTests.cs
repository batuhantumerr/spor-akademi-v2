using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Tenancy;

/// <summary>
/// P1-35, NFR-SEC-08, US-02 (DEC-230). School B calls the API with school A's ids and must get the owning
/// module's "not found": every endpoint with a route parameter (route probes), and every write that names
/// other records in its body (body probes). No row of any school may change, and no list school B can read
/// may contain school A's data. The endpoints come from the running API; guard tests fail when an endpoint
/// is added without a probe or an explicit allowlist entry, so the suite cannot silently fall behind.
/// </summary>
[Trait("Story", "US-02")]
public sealed partial class CrossTenantEndpointTests(DatabaseFixture database) : BillingTest(database)
{
    private const string RecordedSessionKey = "2027-01-14T10:00";

    /// <summary>Platform Admin endpoints with route parameters: school users are refused by role (403).</summary>
    private static readonly string[] PlatformEndpoints =
    [
        "GET /api/v1/platform/schools/{id:guid}",
        "POST /api/v1/platform/schools/{schoolId:guid}/admins/{userId:guid}/reset-password",
    ];

    /// <summary>Writes without a route parameter whose body names no other record, so there is no foreign id to try.</summary>
    private static readonly Dictionary<string, string> WritesWithoutForeignIds = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/auth/login"] = "anonymous; e-mail and password only",
        ["POST /api/v1/auth/refresh"] = "anonymous; refresh cookie only",
        ["POST /api/v1/auth/logout"] = "anonymous; refresh cookie only",
        ["POST /api/v1/auth/change-password"] = "the caller's own password",
        ["POST /api/v1/sports"] = "name only",
        ["POST /api/v1/facilities"] = "name, type, address, capacity",
        ["POST /api/v1/coaches"] = "names, e-mail, phone",
        ["POST /api/v1/platform/schools"] = "Platform Admin only; creates a school",
    };

    /// <summary>Endpoints any signed-in user may call, so they carry no role metadata.</summary>
    private static readonly string[] AnyRoleEndpoints =
    [
        "GET /api/v1/me",
        "POST /api/v1/auth/change-password",
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
    public async Task Another_schools_admin_gets_not_found_from_every_endpoint_with_an_id_and_nothing_changes()
    {
        var before = await FingerprintAsync();
        var failures = new List<string>();

        foreach (var endpoint in RouteProbedEndpoints())
        {
            failures.AddRange(await ProbeAsync(_b.Admin, endpoint.Method, UrlOf(endpoint), RouteProbes(_a, _b)[endpoint.Key]));
        }

        failures.ShouldBeEmpty();
        (await FingerprintAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Another_schools_coach_gets_not_found_from_every_endpoint_with_an_id_a_coach_may_call()
    {
        var before = await FingerprintAsync();
        var failures = new List<string>();

        foreach (var endpoint in RouteProbedEndpoints().Where(e => e.Roles.Contains(nameof(UserRole.Coach))))
        {
            failures.AddRange(await ProbeAsync(_b.Coach, endpoint.Method, UrlOf(endpoint), RouteProbes(_a, _b)[endpoint.Key]));
        }

        failures.ShouldBeEmpty();
        (await FingerprintAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Another_schools_writes_naming_this_schools_records_in_the_body_are_not_found()
    {
        var before = await FingerprintAsync();
        var failures = new List<string>();

        foreach (var (key, probes) in BodyProbes(_a, _b))
        {
            var (method, url) = (key.Split(' ')[0], key.Split(' ')[1]);
            foreach (var probe in probes)
            {
                failures.AddRange(await ProbeAsync(_b.Admin, method, url, probe));
            }
        }

        failures.ShouldBeEmpty();
        (await FingerprintAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Lists_and_searches_of_another_school_hold_none_of_this_schools_data()
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

                leaks.AddRange(_a.LeakMarkers.Where(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase)).Select(marker => $"{name} GET {url} contains {marker}"));
            }
        }

        leaks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Control_the_owning_school_sees_its_data_in_the_lists()
    {
        // Without this, an empty list (or a wrong query) would make the leak test pass for nothing. B's coach has
        // a B group too, so the /me lists it reads are not empty either.
        using var ayse = CoachClient(Ayse);
        var expected = new (HttpClient Client, string Url, Guid Id)[]
        {
            (Admin, "/api/v1/students?search=Kaynak", _a.Student),
            (Admin, "/api/v1/payments", _a.Payment),
            (Admin, "/api/v1/coaches", _a.Coach),
            (Admin, "/api/v1/groups", _a.Group),
            (Admin, "/api/v1/sports", _a.Sport),
            (Admin, "/api/v1/facilities", _a.Facility),
            (Admin, "/api/v1/guardians" + ListQueries["/api/v1/guardians"], _a.Guardian),
            (Admin, "/api/v1/dues/outstanding", _a.UnpaidStudent),
            (Admin, "/api/v1/dashboard", _a.Group),
            (ayse, "/api/v1/me", _a.Coach),
            (ayse, "/api/v1/me/groups", _a.Group),
            (ayse, "/api/v1/me/sessions", _a.Group),
            (ayse, "/api/v1/me/announcements", _a.Announcement),
            (_b.Coach, "/api/v1/me/groups", _b.Group),
            (_b.Coach, "/api/v1/me/sessions", _b.Group),
        };
        var missing = new List<string>();

        foreach (var (client, url, id) in expected)
        {
            var body = await client.GetStringAsync(new Uri(url, UriKind.Relative), Ct);
            if (!body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                missing.Add($"GET {url} lacks {id}");
            }
        }

        missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task Control_the_same_reads_succeed_for_the_owning_school()
    {
        // Without this, a wrong URL or id in a probe would also answer 404 and the suite would pass for nothing.
        var failures = new List<string>();
        foreach (var endpoint in RouteProbedEndpoints().Where(e => e.Method == "GET"))
        {
            var url = UrlOf(endpoint) + RouteProbes(_a, _b)[endpoint.Key].Query;
            var response = await Admin.GetAsync(new Uri(url, UriKind.Relative), Ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                failures.Add($"GET {url}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Every_endpoint_with_a_route_parameter_has_a_route_probe()
    {
        var covered = RouteProbes(_a, _b).Keys.Concat(PlatformEndpoints).ToHashSet(StringComparer.Ordinal);
        var actual = Endpoints().Where(e => e.Parameters.Count > 0).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        actual.Except(covered).ShouldBeEmpty("Add a route probe to CrossTenantEndpointTests for each new endpoint with a route parameter.");
        covered.Except(actual).ShouldBeEmpty("Remove probes of endpoints that no longer exist.");
    }

    [Fact]
    public void Every_write_is_route_probed_body_probed_or_allowlisted()
    {
        var writes = Endpoints().Where(e => e.Method != "GET").ToList();
        var bodyProbed = BodyProbes(_a, _b).Keys.ToHashSet(StringComparer.Ordinal);

        writes.Where(e => e.Parameters.Count == 0 && !bodyProbed.Contains(e.Key) && !WritesWithoutForeignIds.ContainsKey(e.Key))
            .Select(e => e.Key)
            .ShouldBeEmpty("A new write without a route parameter needs a body probe (if its body names other records) or an allowlist entry with a reason.");
        bodyProbed.Concat(WritesWithoutForeignIds.Keys).Except(writes.Where(e => e.Parameters.Count == 0).Select(e => e.Key))
            .ShouldBeEmpty("Remove body probes or allowlist entries of endpoints that no longer exist.");
    }

    [Fact]
    public void Every_school_endpoint_declares_its_roles_and_the_coach_surface_is_pinned()
    {
        var school = Endpoints().Where(e => !e.IsAnonymous && !e.Route.StartsWith("/api/v1/platform/", StringComparison.Ordinal)).ToList();

        school.Where(e => e.Roles.Count == 0 && !AnyRoleEndpoints.Contains(e.Key)).Select(e => e.Key).ShouldBeEmpty();
        school.Where(e => e.Roles.Contains(nameof(UserRole.Coach))).Select(e => e.Key).ShouldBe(
        [
            "GET /api/v1/groups/{id:guid}",
            "GET /api/v1/groups/{id:guid}/sessions",
            "GET /api/v1/groups/{id:guid}/roster",
            "GET /api/v1/groups/{id:guid}/announcements",
            "GET /api/v1/groups/{id:guid}/attendance",
            "GET /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance",
            "PUT /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance",
            "GET /api/v1/students/{id:guid}/attendance",
            "GET /api/v1/me/groups",
            "GET /api/v1/me/sessions",
            "GET /api/v1/me/announcements",
        ], ignoreOrder: true);
    }

    [Fact]
    public void Every_collection_endpoint_is_checked_for_leaks()
    {
        // Parameterless GETs school users can call: each one is read by the leak test above.
        CollectionEndpoints().Select(e => e.Route).ShouldBe(
        [
            "/api/v1/coaches",
            "/api/v1/dashboard",
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

    /// <summary>Sends the probe; only the expected "not found" status and code pass.</summary>
    private static async Task<IEnumerable<string>> ProbeAsync(HttpClient client, string method, string url, Probe probe)
    {
        var target = url + probe.Query;
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(target, UriKind.Relative));
        if (probe.Body is not null)
        {
            request.Content = JsonContent.Create(probe.Body);
        }

        var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        string? code = null;
        if (body.Length > 0 && body.TrimStart().StartsWith('{'))
        {
            using var json = JsonDocument.Parse(body);
            code = json.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        }

        return (int)response.StatusCode == probe.Status && code == probe.Code
            ? []
            : [$"{probe.Name}: {method} {target} expected {probe.Status} {probe.Code}, got {(int)response.StatusCode} {code} {body}"];
    }

    /// <summary>
    /// The concrete URL: every route parameter is replaced from a map by name; an unknown name throws, and the
    /// result may hold no template syntax, so a probe can never pass on a literal route that matches nothing.
    /// </summary>
    private string UrlOf(ApiEndpoint endpoint)
    {
        var url = RouteParameter().Replace(endpoint.Route, match => match.Groups["name"].Value switch
        {
            "id" => ResourceId(endpoint).ToString(),
            "sessionKey" => RecordedSessionKey,
            var other => throw new InvalidOperationException($"{endpoint.Key}: no value for route parameter '{other}'."),
        });
        url.ShouldNotContain("{", customMessage: endpoint.Key);
        url.ShouldNotContain(":guid", customMessage: endpoint.Key);
        return url;
    }

    /// <summary>School A's id for the resource the route names (the first segment after /api/v1).</summary>
    private Guid ResourceId(ApiEndpoint endpoint) => endpoint.Route.Split('/', StringSplitOptions.RemoveEmptyEntries)[2] switch
    {
        "sports" => _a.Sport,
        "facilities" => _a.Facility,
        "coaches" => _a.Coach,
        "groups" => _a.Group,
        "students" => _a.Student,
        "guardians" => _a.Guardian,
        "enrollments" => _a.Enrollment,
        "payments" => _a.Payment,
        var other => throw new InvalidOperationException($"{endpoint.Key}: no school A resource for '{other}'."),
    };

    /// <summary>The "not found" each resource's module answers with.</summary>
    private static string NotFoundOf(string route) => route.Split('/', StringSplitOptions.RemoveEmptyEntries)[2] switch
    {
        "sports" => "Sport.NotFound",
        "facilities" => "Facility.NotFound",
        "coaches" => "User.NotFound",
        "groups" => "Group.NotFound",
        "students" => "Student.NotFound",
        "guardians" => "Guardian.NotFound",
        "enrollments" => "Enrollment.NotFound",
        "payments" => "Payment.NotFound",
        var other => throw new InvalidOperationException($"No not-found code for '{other}'."),
    };

    private List<ApiEndpoint> RouteProbedEndpoints() =>
        [.. Endpoints().Where(e => e.Parameters.Count > 0 && !PlatformEndpoints.Contains(e.Key))];

    /// <summary>
    /// A request per endpoint that passes validation, so only the foreign route id can stop it. Bodies refer to
    /// school B's own records; versions are plausible but irrelevant (the row is not found).
    /// </summary>
    private static Dictionary<string, Probe> RouteProbes(SchoolA a, SchoolB b)
    {
        var probes = new Dictionary<string, (object? Body, string Query)>(StringComparer.Ordinal)
        {
            ["GET /api/v1/sports/{id:guid}"] = (null, ""),
            ["PUT /api/v1/sports/{id:guid}"] = (new { name = "Hack", version = 1 }, ""),
            ["POST /api/v1/sports/{id:guid}/archive"] = (null, ""),
            ["DELETE /api/v1/sports/{id:guid}"] = (null, ""),
            ["GET /api/v1/facilities/{id:guid}"] = (null, ""),
            ["PUT /api/v1/facilities/{id:guid}"] = (new { name = "Hack", type = "Field", address = Address, capacity = 10, version = 1 }, ""),
            ["POST /api/v1/facilities/{id:guid}/archive"] = (null, ""),
            ["DELETE /api/v1/facilities/{id:guid}"] = (null, ""),
            ["GET /api/v1/coaches/{id:guid}"] = (null, ""),
            ["PUT /api/v1/coaches/{id:guid}"] = (new { firstName = "Hack", lastName = "Deneme", phone = (string?)null, version = 1 }, ""),
            ["POST /api/v1/coaches/{id:guid}/deactivate"] = (null, ""),
            ["POST /api/v1/coaches/{id:guid}/reset-password"] = (null, ""),
            ["GET /api/v1/groups/{id:guid}"] = (null, ""),
            ["PUT /api/v1/groups/{id:guid}"] = (new
            {
                name = "Hack",
                birthYearRange = new { from = 2015, to = 2016 },
                capacity = 20,
                facilityId = b.Facility,
                coachId = (Guid?)null,
                version = 1,
            }, ""),
            ["PUT /api/v1/groups/{id:guid}/schedule"] = (new { slots = new[] { new { day = "Monday", start = "08:00", end = "09:00" } }, version = 1 }, ""),
            ["POST /api/v1/groups/{id:guid}/archive"] = (null, ""),
            ["GET /api/v1/groups/{id:guid}/sessions"] = (null, "?from=2027-01-01&to=2027-01-31"),
            ["GET /api/v1/groups/{id:guid}/fees"] = (null, ""),
            ["POST /api/v1/groups/{id:guid}/fees"] = (new { effectiveFrom = "2027-02", amount = new { amount = "1.00", currency = "TRY" } }, ""),
            ["GET /api/v1/groups/{id:guid}/enrollments"] = (null, ""),
            ["GET /api/v1/groups/{id:guid}/roster"] = (null, ""),
            ["GET /api/v1/groups/{id:guid}/announcements"] = (null, ""),
            ["POST /api/v1/groups/{id:guid}/announcements"] = (new { title = "Hack", body = "Hack" }, ""),
            ["GET /api/v1/groups/{id:guid}/attendance"] = (null, "?month=2027-01"),
            ["GET /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance"] = (null, ""),
            ["PUT /api/v1/groups/{id:guid}/sessions/{sessionKey}/attendance"] = (new { records = new[] { new { studentId = b.Student, status = "Absent" } } }, ""),
            ["GET /api/v1/students/{id:guid}"] = (null, ""),
            ["PUT /api/v1/students/{id:guid}"] = (new
            {
                firstName = "Hack",
                lastName = "Deneme",
                birthDate = "1990-01-01",
                gender = "Male",
                phone = "0533 999 99 99",
                emergencyContact = new { name = "Acil Kişi", phone = "0533 999 99 98", relationship = "Komşu" },
                version = 1,
            }, ""),
            ["PUT /api/v1/students/{id:guid}/guardians"] = (new { guardians = Array.Empty<object>(), version = 1 }, ""),
            ["POST /api/v1/students/{id:guid}/leave"] = (new { date = "2027-01-14" }, ""),
            ["POST /api/v1/students/{id:guid}/reactivate"] = (null, ""),
            ["GET /api/v1/students/{id:guid}/enrollments"] = (null, ""),
            ["GET /api/v1/students/{id:guid}/attendance"] = (null, "?from=2027-01-01&to=2027-01-31"),
            ["PUT /api/v1/guardians/{id:guid}"] = (new { firstName = "Hack", lastName = "Deneme", phone = "0533 999 99 97", version = 1 }, ""),
            ["GET /api/v1/enrollments/{id:guid}"] = (null, ""),
            ["POST /api/v1/enrollments/{id:guid}/end"] = (new { date = "2027-01-14" }, ""),
            ["GET /api/v1/payments/{id:guid}"] = (null, ""),
            ["POST /api/v1/payments/{id:guid}/cancel"] = (new { reason = "Hack" }, ""),
        };
        return probes.ToDictionary(p => p.Key, p => new Probe(p.Key, p.Value.Body, p.Value.Query, 404, NotFoundOf(p.Key.Split(' ')[1])), StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes without a route parameter whose body names other records: one probe per foreign reference,
    /// every other reference pointing at school B's own records, so exactly one id is foreign.
    /// </summary>
    private static Dictionary<string, Probe[]> BodyProbes(SchoolA a, SchoolB b)
    {
        object Group(Guid sport, Guid facility, Guid? coach) => new
        {
            sportId = sport,
            name = "Sızma Grubu",
            birthYearRange = new { from = 2015, to = 2016 },
            capacity = 20,
            facilityId = facility,
            coachId = coach,
        };

        object Student(Guid guardian) => new
        {
            firstName = "Sızma",
            lastName = "Deneme",
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = "0533 999 99 96", relationship = "Komşu" },
            guardians = new[] { new { guardianId = guardian, relationship = "Mother" } },
        };

        return new(StringComparer.Ordinal)
        {
            ["POST /api/v1/groups"] =
            [
                new("A's sport", Group(a.Sport, b.Facility, null), Status: 404, Code: "Sport.NotFound"),
                new("A's facility", Group(b.Sport, a.Facility, null), Status: 404, Code: "Facility.NotFound"),
                // The group module reports an unknown coach as a business rule (appendix E).
                new("A's coach", Group(b.Sport, b.Facility, a.Coach), Status: 422, Code: "Group.CoachNotFound"),
            ],
            ["POST /api/v1/students"] = [new("A's guardian", Student(a.Guardian), Status: 404, Code: "Guardian.NotFound")],
            ["POST /api/v1/enrollments"] =
            [
                new("A's student", new { studentId = a.Student, groupId = b.Group, startDate = "2027-01-14" }, Status: 404, Code: "Student.NotFound"),
                new("A's group", new { studentId = b.Student, groupId = a.Group, startDate = "2027-01-14" }, Status: 404, Code: "Group.NotFound"),
            ],
            ["POST /api/v1/payments"] =
            [
                new("A's enrollment", new { enrollmentId = a.Enrollment, period = "2027-01", amount = new { amount = "1000.00", currency = "TRY" }, method = "Cash", paidOn = "2027-01-10" }, Status: 404, Code: "Enrollment.NotFound"),
            ],
        };
    }

    /// <summary>Query strings a list needs to answer at all; the guardian search uses school A's real phone.</summary>
    private Dictionary<string, string> ListQueries => new(StringComparer.Ordinal)
    {
        ["/api/v1/guardians"] = "?phone=" + Uri.EscapeDataString(_a.GuardianPhone),
        ["/api/v1/students"] = "?search=Kaynak",
    };

    private static readonly object Address = new { line = "Atatürk Cad. No:1", district = "Kadıköy", city = "İstanbul" };

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(:[^}]*)?\}")]
    private static partial Regex RouteParameter();

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

    /// <summary>Parameterless GETs a school user can call (not anonymous, not Platform Admin only).</summary>
    private List<ApiEndpoint> CollectionEndpoints() =>
    [
        .. Endpoints().Where(e => e.Method == "GET" && e.Parameters.Count == 0 && !e.IsAnonymous
            && (e.Roles.Count == 0 || e.Roles.Contains(nameof(UserRole.SchoolAdmin)) || e.Roles.Contains(nameof(UserRole.Coach)))),
    ];

    /// <summary>
    /// A fingerprint of every school's rows: each table with a <c>tenant_id</c> column, whole, plus the schools,
    /// read directly as the migrator (no tenant filter). Catches a write into school A and also into school B.
    /// </summary>
    private async Task<string> FingerprintAsync()
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
            // Table names come from the catalog above, not from input.
#pragma warning disable CA2100
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM {table} t",
                connection);
#pragma warning restore CA2100
            fingerprint.Append(table).Append('=').Append(await command.ExecuteScalarAsync(Ct)).Append('\n');
        }

        return fingerprint.ToString();
    }

    /// <summary>One of every resource in school A, created over HTTP like real use, plus an unpaid month.</summary>
    private async Task<SchoolA> SeedSchoolAAsync()
    {
        var group = IdOf(await GroupAsync("U10 Futbol", 20, Ayse, Slot("Thursday", "10:00", "11:00")));
        await DefineFeeOkAsync(group, "2027-01", "1000.00");
        var student = await RegisterStudentAsync("Kaynak", "Okulu");
        var studentDto = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var guardian = studentDto.GetProperty("guardians")[0];
        var enrollment = IdOf(await EnrollOkAsync(student, group, "2027-01-04"));
        var payment = IdOf(await RecordOkAsync(enrollment, "2027-01", "1000.00"));
        var unpaid = await RegisterStudentAsync("Borçlu", "Okulu");
        await EnrollOkAsync(unpaid, group, "2027-01-04");
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
            unpaid,
            guardian.GetProperty("guardianId").GetGuid(),
            guardian.GetProperty("phone").GetString()!,
            enrollment,
            payment,
            IdOf(await BodyOf(announcement)));
    }

    /// <summary>
    /// School B: a real admin user, a coach leading a group that trains today (so B's /me lists are not empty),
    /// a student, and the sport and facility its request bodies refer to.
    /// </summary>
    private async Task<SchoolB> SeedSchoolBAsync()
    {
        var school = await TestSchools.CreateAsync(Api, "okul-b");
        var adminUser = await SeedUserAsync("yonetici@okul-b.test", UserRole.SchoolAdmin, school);
        var admin = Api.CreateClient();
        admin.Authorize(TestTokens.Create(adminUser.Id.Value, nameof(UserRole.SchoolAdmin), school.Value, Api.Clock.GetUtcNow()));
        var coachUser = await SeedCoachAsync(school);
        var sport = await CreateSportAsync(admin, "Yüzme");
        var facility = await CreateFacilityAsync(admin, "B Havuzu");
        var group = await PostGroupAsync(NewGroup("B Grubu", coachUser.Id.Value, facility, sport), admin);
        group.StatusCode.ShouldBe(HttpStatusCode.Created);
        var groupId = IdOf(await BodyOf(group));
        (await admin.PutAsJsonAsync($"/api/v1/groups/{groupId}/schedule", new { slots = new[] { Slot("Thursday", "18:00", "19:00") }, version = VersionOf(await GetGroupAsync(groupId, admin)) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var student = await RegisterStudentAsync("Komşu", "Okul", client: admin);
        return new SchoolB(admin, CoachClient(coachUser), sport, facility, groupId, student);
    }

    private sealed record ApiEndpoint(string Method, string Route, IReadOnlyList<string> Parameters, bool IsAnonymous, IReadOnlyCollection<string> Roles)
    {
        public string Key => $"{Method} {Route}";
    }

    /// <summary>A request and the "not found" it must get.</summary>
    private sealed record Probe(string Name, object? Body, string Query = "", int Status = 404, string Code = "");

    private sealed record SchoolA(
        Guid Sport,
        Guid Facility,
        Guid Coach,
        Guid Group,
        Guid Student,
        Guid UnpaidStudent,
        Guid Guardian,
        string GuardianPhone,
        Guid Enrollment,
        Guid Payment,
        Guid Announcement)
    {
        /// <summary>Ids and personal data of school A that must never appear in school B's responses.</summary>
        public string[] LeakMarkers =>
        [
            .. new[] { Sport, Facility, Coach, Group, Student, UnpaidStudent, Guardian, Enrollment, Payment, Announcement }.Select(id => id.ToString()),
            GuardianPhone.TrimStart('+'), // JSON may escape the '+'
            "Kaynak",
            "Borçlu",
        ];
    }

    private sealed record SchoolB(HttpClient Admin, HttpClient Coach, Guid Sport, Guid Facility, Guid Group, Guid Student);
}

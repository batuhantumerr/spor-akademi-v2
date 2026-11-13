using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Observability;

/// <summary>
/// P4-07 (NFR-OBS-04, DEC-550 – DEC-553): what the API records as spans and metrics, read with OpenTelemetry's in-memory
/// exporters instead of an OTLP endpoint. Spans must never carry personal data, query strings, headers, cookies, tokens
/// or SQL parameter values; metrics carry fixed, low-cardinality attributes and no tenant.
/// </summary>
[Trait("Story", "P4-07")]
public sealed class TelemetryTests(DatabaseFixture database) : IdentityTest(database)
{
    private const string Email = "iz.gizli.kullanici@example.com";
    private const string WrongPassword = "Yanlis-Iz-Sifresi-41";
    private const string SearchTerm = "Izsoyadligizli";
    private const string HubSecret = "hub-secret-p407";
    private const string ResetSecret = "reset-secret-p407";
    private const string HeaderSecret = "header-secret-p407";
    private const string CookieSecret = "cookie-secret-p407";
    private const string BadUuid = "uuid-gizli-deger-p407";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Spans_carry_no_personal_data_query_strings_headers_or_sql_values()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var api = TelemetryApi(spans: spans, services: s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new DuplicateUserEndpoint()));
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var school = await TestSchools.CreateAsync(Api, "iz-okulu");
        await SeedUserAsync(Email, UserRole.SchoolAdmin, school);

        // A failed login with a secret header and cookie, and a correlation id.
        using (var failed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = Email, password = WrongPassword }) })
        {
            failed.Headers.Add("X-Correlation-Id", "corr-p407-login");
            failed.Headers.Add("Cookie", $"sa_refresh={CookieSecret}");
            failed.Headers.Add("X-Api-Secret", HeaderSecret);
            (await client.SendAsync(failed, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = OwnPassword }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await SessionFromAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        // A search (an SQL parameter), the hub's query token, a token in another query, and a database error.
        (await client.GetAsync(new Uri($"/api/v1/students?search={SearchTerm}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PostAsync(new Uri($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={HubSecret}", UriKind.Relative), null, Ct);
        await client.GetAsync(new Uri($"/api/v1/me?token={ResetSecret}&email={Uri.EscapeDataString(Email)}", UriKind.Relative), Ct);
        (await client.PostAsJsonAsync($"/test/duplicate-user?school={school.Value}", new { email = Email }, Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        // PostgreSQL quotes the rejected value in its error message (22P02).
        (await client.PostAsJsonAsync("/test/bad-uuid", new { value = BadUuid }, Ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        var recorded = await SettledAsync(api, spans, s => s.Kind == ActivityKind.Server && s.GetTagItem("url.path") as string == "/test/bad-uuid");

        // Proof that the interesting spans exist, otherwise the checks below prove nothing.
        var loginSpan = recorded.Single(s => s.Kind == ActivityKind.Server && Equals(s.GetTagItem("spor_akademi.correlation_id"), "corr-p407-login"));
        loginSpan.GetTagItem("http.route").ShouldBe("/api/v1/auth/login");
        recorded.Any(s => s.Source.Name == "Npgsql" && s.GetTagItem("db.query.text") is string sql && sql.Contains("users", StringComparison.Ordinal)).ShouldBeTrue();
        // P4-04: each connection open sets the session's school for row level security; its span shows the parameterised
        // statement only, never the school id (DEC-551).
        recorded.Any(s => s.Source.Name == "Npgsql" && s.GetTagItem("db.query.text") is string sql && sql.Contains("set_config", StringComparison.Ordinal)).ShouldBeTrue();
        // A failed command's span carries the driver's exception event (with PostgreSQL's message); it is not exported,
        // and the request's span keeps the error's SQLSTATE instead (DEC-551).
        recorded.ShouldNotContain(s => s.Events.Any(e => e.Name == "exception"));
        recorded.Single(s => s.Kind == ActivityKind.Server && Equals(s.GetTagItem("url.path"), "/test/bad-uuid"))
            .GetTagItem("db.response.status_code").ShouldBe("22P02");
        recorded.Single(s => s.Kind == ActivityKind.Server && Equals(s.GetTagItem("url.path"), "/test/duplicate-user"))
            .GetTagItem("db.response.status_code").ShouldBe("23505");
        recorded.ShouldContain(s => Equals(s.GetTagItem("url.path"), "/hubs/notifications/negotiate"));

        recorded.ShouldNotContain(s => s.GetTagItem("url.query") != null);
        string[] forbidden =
        [
            Email, "iz.gizli", BadUuid, WrongPassword, OwnPassword, SearchTerm, HubSecret, ResetSecret, HeaderSecret, CookieSecret,
            session.AccessToken, session.RefreshToken, session.AccessToken.Split('.')[2],
            school.Value.ToString(), school.Value.ToString("N"),
        ];
        foreach (var span in recorded)
        {
            var text = Describe(span);
            foreach (var value in forbidden)
            {
                text.ShouldNotContain(value, Case.Insensitive, $"'{value}' leaked into span {span.Source.Name}/{span.DisplayName}.");
            }
        }
    }

    [Fact]
    public async Task Log_events_carry_the_trace_id_of_the_request_span()
    {
        var spans = new ConcurrentQueue<Activity>();
        var sink = new TraceIdSink();
        using var api = TelemetryApi(spans: spans, configuration: new Dictionary<string, string?>
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
        }, services: s => s.AddSingleton<ILogEventSink>(sink));
        using var client = api.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Add("X-Correlation-Id", "corr-p407-ready");
        (await client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var recorded = await SettledAsync(api, spans, s => Equals(s.GetTagItem("spor_akademi.correlation_id"), "corr-p407-ready"));
        var span = recorded.Single(s => Equals(s.GetTagItem("spor_akademi.correlation_id"), "corr-p407-ready"));
        sink.Events.ShouldContain(e => e.CorrelationId == "corr-p407-ready" && e.TraceId == span.TraceId.ToHexString());
    }

    [Fact]
    public async Task Metrics_count_failed_logins_by_code_without_tenant_or_personal_attributes()
    {
        var metrics = new List<Metric>();
        using var api = TelemetryApi(metrics: metrics);
        using var client = api.CreateClient();
        var school = await TestSchools.CreateAsync(Api, "iz-metrik");
        await SeedUserAsync(Email, UserRole.SchoolAdmin, school);

        (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = Email, password = WrongPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        api.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();

        var failures = Points(metrics, "spor_akademi.auth.login.failures");
        failures.ShouldContain(p => p.Tags.Single().Key == "reason" && Equals(p.Tags.Single().Value, "Auth.InvalidCredentials"));
        var requests = Points(metrics, "http.server.request.duration");
        requests.ShouldContain(p => p.Tags.Any(t => t.Key == "http.route" && Equals(t.Value, "/api/v1/auth/login")));
        foreach (var point in metrics.SelectMany(m => Points(metrics, m.Name)))
        {
            point.Tags.ShouldNotContain(t => t.Key.Contains("tenant", StringComparison.OrdinalIgnoreCase) || t.Key.Contains("user", StringComparison.OrdinalIgnoreCase));
            point.Tags.ShouldNotContain(t => (t.Value as string ?? string.Empty).Contains("example.com", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Without_an_endpoint_nothing_is_recorded()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var api = TelemetryApi(spans: spans, configuration: new Dictionary<string, string?> { ["OpenTelemetry:Enabled"] = null });
        using var client = api.CreateClient();

        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush().ShouldBeTrue();

        spans.ShouldBeEmpty();
    }

    [Fact]
    public void Settings_read_the_endpoint_from_the_section_or_the_standard_variable()
    {
        static SporAkademi.Api.Observability.TelemetrySettings Read(params (string Key, string Value)[] values) =>
            SporAkademi.Api.Observability.TelemetrySettings.From(new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

        Read().Enabled.ShouldBeFalse();
        Read().Exporter("v1/traces").ShouldBeNull();
        Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317")).Exporter("v1/traces")!.Endpoint.ShouldBe(new Uri("http://127.0.0.1:4317"));
        var http = Read(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318"), ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"));
        http.Enabled.ShouldBeTrue();
        http.Exporter("v1/metrics")!.Endpoint.ShouldBe(new Uri("http://127.0.0.1:4318/v1/metrics"));
        // A path-based endpoint keeps its path.
        Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4318/otlp"), ("OpenTelemetry:Otlp:Protocol", "http/protobuf"))
            .Exporter("v1/traces")!.Endpoint.ShouldBe(new Uri("http://127.0.0.1:4318/otlp/v1/traces"));
        Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4318/otlp/"), ("OpenTelemetry:Otlp:Protocol", "http/protobuf"))
            .Exporter("v1/traces")!.Endpoint.ShouldBe(new Uri("http://127.0.0.1:4318/otlp/v1/traces"));

        // An invalid value turns telemetry off with a reason instead of stopping the API.
        foreach (var invalid in new[]
                 {
                     Read(("OpenTelemetry:Otlp:Endpoint", "file:///etc/passwd")),
                     Read(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4317"), ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Tracing:SampleRatio", "2")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Tracing:SampleRatio", "abc")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Metrics:ExportInterval", "abc")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Metrics:ExportInterval", "00:00:00")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Metrics:HealthInterval", "-00:00:30")),
                     Read(("OpenTelemetry:Otlp:Endpoint", "http://127.0.0.1:4317"), ("OpenTelemetry:Enabled", "evet")),
                 })
        {
            invalid.Enabled.ShouldBeFalse();
            invalid.Exporter("v1/traces").ShouldBeNull();
            invalid.Problem.ShouldNotBeNull();
        }
    }

    [Fact]
    public void A_remote_callers_trace_id_and_sampled_flag_do_not_decide_sampling()
    {
        var sampler = new SporAkademi.Api.Observability.ApiSampler(0.5);
        var remote = new ActivityContext(ActivityTraceId.CreateFromString("00000000000000000000000000000001"), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        var decisions = Enumerable.Range(0, 200)
            .Select(_ => sampler.ShouldSample(new SamplingParameters(remote, remote.TraceId, "GET /api/v1/me", ActivityKind.Server)).Decision)
            .ToHashSet();

        // The same chosen trace id, sampled by the caller, is sometimes dropped and sometimes recorded.
        decisions.ShouldBe([SamplingDecision.Drop, SamplingDecision.RecordAndSample], ignoreOrder: true);
        new SporAkademi.Api.Observability.ApiSampler(0).ShouldSample(new SamplingParameters(remote, remote.TraceId, "x", ActivityKind.Server))
            .Decision.ShouldBe(SamplingDecision.Drop);
    }

    [Theory]
    [InlineData("OpenTelemetry:Otlp:Protocol", "http/json")]
    [InlineData("OpenTelemetry:Metrics:ExportInterval", "abc")]
    [InlineData("OpenTelemetry:Metrics:ExportInterval", "00:00:00")]
    [InlineData("OpenTelemetry:Metrics:HealthInterval", "-00:00:30")]
    [InlineData("OpenTelemetry:Tracing:SampleRatio", "abc")]
    [InlineData("OpenTelemetry:Enabled", "evet")]
    public async Task A_malformed_setting_turns_telemetry_off_and_the_api_still_starts(string key, string value)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var api = TelemetryApi(spans: spans, configuration: new Dictionary<string, string?>
        {
            ["OpenTelemetry:Otlp:Endpoint"] = "http://127.0.0.1:9",
            ["OpenTelemetry:Enabled"] = null,
            [key] = value,
        });
        using var client = api.CreateClient();

        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush().ShouldBeTrue();

        spans.ShouldBeEmpty();
    }

    /// <summary>The API with telemetry on (no OTLP endpoint) and in-memory exporters.</summary>
    private WebApplicationFactory<Program> TelemetryApi(
        ConcurrentQueue<Activity>? spans = null,
        List<Metric>? metrics = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null) =>
        Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config
                .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenTelemetry:Enabled"] = "true" })
                .AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()));
            builder.ConfigureTestServices(collection =>
            {
                if (spans is not null)
                {
                    collection.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(new QueueExporter(spans))));
                }

                if (metrics is not null)
                {
                    collection.ConfigureOpenTelemetryMeterProvider(meter => meter.AddInMemoryExporter(metrics));
                }

                services?.Invoke(collection);
            });
        });

    /// <summary>Waits until the span matching <paramref name="last"/> has ended (the server span ends after the client has its response).</summary>
    private static async Task<IReadOnlyList<Activity>> SettledAsync(WebApplicationFactory<Program> api, ConcurrentQueue<Activity> spans, Func<Activity, bool> last)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!spans.Any(last) && DateTime.UtcNow < deadline)
        {
            api.Services.GetRequiredService<TracerProvider>().ForceFlush();
            await Task.Delay(50, Ct);
        }

        spans.ShouldContain(s => last(s), "The expected span was never exported.");
        return [.. spans];
    }

    private static string Describe(Activity span)
    {
        var text = new StringBuilder();
        text.Append(span.DisplayName).Append(' ').Append(span.StatusDescription).Append(' ');
        foreach (var (key, value) in span.TagObjects)
        {
            text.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(' ');
        }

        foreach (var spanEvent in span.Events)
        {
            text.Append(spanEvent.Name).Append(' ');
            foreach (var (key, value) in spanEvent.Tags)
            {
                text.Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(' ');
            }
        }

        foreach (var (key, value) in span.Baggage)
        {
            text.Append(key).Append('=').Append(value).Append(' ');
        }

        return text.ToString();
    }

    private static List<(string Name, KeyValuePair<string, object?>[] Tags)> Points(List<Metric> metrics, string name)
    {
        var points = new List<(string, KeyValuePair<string, object?>[])>();
        foreach (var metric in metrics.Where(m => m.Name == name))
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new List<KeyValuePair<string, object?>>();
                foreach (var tag in point.Tags)
                {
                    tags.Add(tag);
                }

                points.Add((name, [.. tags]));
            }
        }

        return points;
    }

    private sealed class QueueExporter(ConcurrentQueue<Activity> spans) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                spans.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }

    private sealed class TraceIdSink : ILogEventSink
    {
        public ConcurrentQueue<(string? CorrelationId, string? TraceId)> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            var correlation = logEvent.Properties.TryGetValue("CorrelationId", out var value) && value is ScalarValue { Value: string id } ? id : null;
            Events.Enqueue((correlation, logEvent.TraceId?.ToHexString()));
        }
    }
}

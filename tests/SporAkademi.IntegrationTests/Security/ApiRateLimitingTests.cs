using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Students.Documents;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// NFR-SEC-14 (P4-02, DEC-503 – DEC-506): besides the per-endpoint limits (sign-in, refresh, exports, the parent API),
/// every <c>/api</c> request counts towards the caller's general budget: the signed-in user (token subject), or the
/// client IP when there is no valid token; writes also count towards the school's write budget. The rest of the suite
/// runs with very high limits (ApiFactory); these tests start an API with small ones.
/// </summary>
public sealed class ApiRateLimitingTests(DatabaseFixture database) : IntegrationTest(database)
{
    private static readonly Guid SchoolA = Guid.CreateVersion7();
    private static readonly Guid SchoolB = Guid.CreateVersion7();

    private const string Proxy = "10.0.0.5";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task A_user_over_the_limit_gets_429_with_a_code_and_retry_after()
    {
        using var api = Limited(user: 5);
        using var first = SignedIn(api, SchoolAdmin(SchoolA));
        using var second = SignedIn(api, SchoolAdmin(SchoolA));

        for (var i = 0; i < 5; i++)
        {
            (await first.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        var refused = await first.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct);

        await ShouldBeRateLimitedAsync(refused);
        // Another user of the same school has a budget of their own.
        (await second.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Writes_share_a_budget_per_school()
    {
        using var api = Limited(schoolWrites: 3);
        using var first = SignedIn(api, SchoolAdmin(SchoolA));
        using var colleague = SignedIn(api, SchoolAdmin(SchoolA));
        using var otherSchool = SignedIn(api, SchoolAdmin(SchoolB));

        for (var i = 0; i < 3; i++)
        {
            (await PostSportAsync(first)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }

        await ShouldBeRateLimitedAsync(await PostSportAsync(colleague));
        // Reads are not writes, and another school is not affected.
        (await colleague.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await PostSportAsync(otherSchool)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Anonymous_requests_are_limited_per_client_address()
    {
        using var api = Limited(anonymous: 3);
        using var anonymous = api.CreateClient();
        using var signedIn = SignedIn(api, SchoolAdmin(SchoolA));

        for (var i = 0; i < 3; i++)
        {
            (await anonymous.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await ShouldBeRateLimitedAsync(await anonymous.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct));
        // A signed-in user behind the same address counts on their own budget.
        (await signedIn.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Health_webhooks_and_the_hub_are_outside_the_general_limit()
    {
        using var api = Limited(anonymous: 1);
        using var anonymous = api.CreateClient();
        await anonymous.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct);
        (await anonymous.GetAsync(new Uri("/api/v1/sports", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        for (var i = 0; i < 5; i++)
        {
            (await anonymous.GetAsync(new Uri("/health/live", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            using var webhook = new StringContent("{}", Encoding.UTF8, "application/json");
            (await anonymous.PostAsync(new Uri("/api/v1/payment-webhooks", UriKind.Relative), webhook, Ct)).StatusCode
                .ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Endpoint_limits_answer_with_retry_after_too()
    {
        using var anonymous = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        HttpResponseMessage response = null!;
        for (var i = 0; i < 11; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            response = await anonymous.SendAsync(request, Ct);
        }

        await ShouldBeRateLimitedAsync(response);
    }

    [Fact]
    [Trait("Story", "P4-03")]
    public async Task Uploads_in_flight_are_capped_per_school()
    {
        // A04 (DEC-514): the form is read into memory, so N parallel uploads cost N x 5 MB; one school cannot hold more
        // than the cap at a time and another school is not affected.
        var gate = new GatedUpload();
        using var api = Limited(uploads: 1).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<ICommandHandler<UploadStudentDocumentCommand, StudentDocumentDto>>(_ => gate)));
        using var first = SignedIn(api, SchoolAdmin(SchoolA));
        using var second = SignedIn(api, SchoolAdmin(SchoolA));
        using var otherSchool = SignedIn(api, SchoolAdmin(SchoolB));
        var student = Guid.CreateVersion7();

        // The first upload stays inside the endpoint until the gate opens, so its permit stays taken.
        var inFlight = UploadAsync(first, student);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await ShouldBeRateLimitedAsync(await UploadAsync(second, student));
        // Another school has slots of its own: its upload gets as far as the handler (which is also gated), not a 429.
        var other = UploadAsync(otherSchool, student);

        gate.Open.SetResult();
        (await inFlight).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await other).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        // The permit is back once the first upload is over.
        (await UploadAsync(second, student)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>Stands in for the upload handler: reports that a request got inside, then waits to be released.</summary>
    private sealed class GatedUpload : ICommandHandler<UploadStudentDocumentCommand, StudentDocumentDto>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Result<StudentDocumentDto>> Handle(UploadStudentDocumentCommand command, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Open.Task.WaitAsync(cancellationToken);
            return Result.Failure<StudentDocumentDto>(Error.Validation("Test.Gated", "Stopped by the test."));
        }
    }

    private static Uri UploadUrl(Guid student) => new(string.Create(CultureInfo.InvariantCulture, $"/api/v1/students/{student}/documents"), UriKind.Relative);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid student)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Photo"), "type");
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "photo.png");
        return await client.PostAsync(UploadUrl(student), form, Ct);
    }

    private static async Task ShouldBeRateLimitedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("Request.TooManyRequests");
        response.Headers.TryGetValues("Retry-After", out var values).ShouldBeTrue("Retry-After is missing.");
        var seconds = int.Parse(values!.Single(), CultureInfo.InvariantCulture);
        seconds.ShouldBeInRange(1, 60);
    }

    private static async Task<HttpResponseMessage> PostSportAsync(HttpClient client)
    {
        // Refused by validation; the limiter counts the request before the endpoint runs.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/api/v1/sports", UriKind.Relative), content, Ct);
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public void Limits_must_be_positive()
    {
        using var api = Limited(user: 0);

        Should.Throw<Exception>(() => api.CreateClient()).ToString().ShouldContain("RateLimiting");
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Behind_a_known_proxy_each_forwarded_client_has_its_own_budget()
    {
        using var api = Limited(anonymous: 1, knownProxy: Proxy);
        using var http = api.CreateClient();

        (await AnonymousAsync(http, Proxy, "203.0.113.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeRateLimitedAsync(await AnonymousAsync(http, Proxy, "203.0.113.1"));
        // Another client behind the same proxy is not affected.
        (await AnonymousAsync(http, Proxy, "203.0.113.2")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task X_forwarded_for_from_an_unknown_source_is_ignored()
    {
        using var api = Limited(anonymous: 1, knownProxy: Proxy);
        using var http = api.CreateClient();

        (await AnonymousAsync(http, "198.51.100.7", "203.0.113.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // A client cannot buy a fresh budget by naming another address: it is counted by its own.
        await ShouldBeRateLimitedAsync(await AnonymousAsync(http, "198.51.100.7", "203.0.113.2"));
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Without_a_configured_proxy_no_forwarded_header_is_trusted_not_even_from_loopback()
    {
        using var api = Limited(anonymous: 1);
        using var http = api.CreateClient();

        (await AnonymousAsync(http, "127.0.0.1", "203.0.113.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldBeRateLimitedAsync(await AnonymousAsync(http, "127.0.0.1", "203.0.113.2"));
    }

    [Fact]
    [Trait("Story", "P4-02")]
    public async Task Ipv6_clients_are_counted_per_64_network()
    {
        using var api = Limited(anonymous: 1);
        using var http = api.CreateClient();

        (await AnonymousAsync(http, "2001:db8:0:1::1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Another address of the same /64 shares the budget; the next /64 does not.
        await ShouldBeRateLimitedAsync(await AnonymousAsync(http, "2001:db8:0:1:ffff::2"));
        (await AnonymousAsync(http, "2001:db8:0:2::1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> AnonymousAsync(HttpClient http, string address, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sports").From(address, forwardedFor);
        return await http.SendAsync(request, Ct);
    }

    private string SchoolAdmin(Guid school) =>
        TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", school, Api.Clock.GetUtcNow());

    private static HttpClient SignedIn(WebApplicationFactory<Program> api, string token)
    {
        var client = api.CreateClient();
        client.Authorize(token);
        return client;
    }

    private WebApplicationFactory<Program> Limited(int user = 1_000, int schoolWrites = 1_000, int anonymous = 1_000, string? knownProxy = null, int uploads = 1_000) =>
        Api.WithWebHostBuilder(builder =>
        {
            RemoteAddress.Enable(builder);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RateLimiting:UserPermitsPerMinute"] = user.ToString(CultureInfo.InvariantCulture),
                    ["RateLimiting:SchoolWritePermitsPerMinute"] = schoolWrites.ToString(CultureInfo.InvariantCulture),
                    ["RateLimiting:AnonymousPermitsPerMinute"] = anonymous.ToString(CultureInfo.InvariantCulture),
                    ["RateLimiting:ConcurrentUploadsPerSchool"] = uploads.ToString(CultureInfo.InvariantCulture),
                    ["ForwardedHeaders:KnownProxies:0"] = knownProxy,
                }));
        });
}

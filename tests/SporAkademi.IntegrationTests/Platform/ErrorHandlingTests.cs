using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Api.Errors;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Platform;

public sealed class ErrorHandlingTests(DatabaseFixture database) : IntegrationTest(database)
{
    [Fact]
    public async Task Anonymous_calls_are_denied_by_default_with_a_coded_problem()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Auth.Unauthenticated");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    public static TheoryData<string> InvalidTokenCases() =>
        ["other-key", "expired", "wrong-issuer", "alg-none", "school-role-without-tenant", "platform-admin-with-tenant"];

    [Theory]
    [MemberData(nameof(InvalidTokenCases))]
    public async Task Invalid_tokens_are_rejected(string @case)
    {
        var now = Api.Clock.GetUtcNow();
        var user = Guid.CreateVersion7();
        var school = Guid.CreateVersion7();
        var token = @case switch
        {
            "other-key" => TestTokens.Create(user, "SchoolAdmin", school, now, signingKeyHex: new string('b', 64)),
            "expired" => TestTokens.Create(user, "SchoolAdmin", school, now.AddMinutes(-16)),
            "wrong-issuer" => TestTokens.Create(user, "SchoolAdmin", school, now, issuer: "someone-else"),
            "alg-none" => UnsignedToken(user, school, now),
            "school-role-without-tenant" => TestTokens.Create(user, "SchoolAdmin", null, now),
            _ => TestTokens.Create(user, "PlatformAdmin", school, now),
        };
        using var client = ClientWithTestEndpoints();
        client.Authorize(token);

        var response = await client.GetAsync(new Uri("/test/business-rule", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_token_is_accepted_until_it_expires()
    {
        using var client = ClientWithTestEndpoints();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "Coach", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));

        (await client.GetAsync(new Uri("/test/business-rule", UriKind.Relative), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        Api.Clock.Advance(TimeSpan.FromMinutes(16));
        (await client.GetAsync(new Uri("/test/business-rule", UriKind.Relative), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bodies_over_one_megabyte_are_rejected()
    {
        using var content = new ByteArrayContent(new byte[1_048_577]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var response = await Client.PostAsync(new Uri("/api/v1/anything", UriKind.Relative), content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("code").GetString().ShouldBe("Request.TooLarge");
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id_and_echoes_a_valid_one()
    {
        var generated = await Client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        generated.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBeNullOrWhiteSpace();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "abc-123");
        var echoed = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        echoed.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("abc-123");

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        bad.Headers.Add("X-Correlation-Id", "<script>");
        var replaced = await Client.SendAsync(bad, TestContext.Current.CancellationToken);
        replaced.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBe("<script>");
    }

    [Fact]
    public async Task Unexpected_exceptions_become_a_bare_500_problem()
    {
        using var client = ClientWithTestEndpoints();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));

        var response = await client.GetAsync(new Uri("/test/throw", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("secret detail");
        body.ShouldNotContain("InvalidOperationException");
        using var problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("code").GetString().ShouldBe("Unexpected.Error");
        problem.RootElement.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Expected_errors_become_problems_with_code_args_and_field_errors()
    {
        using var client = ClientWithTestEndpoints();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));

        var response = await client.GetAsync(new Uri("/test/business-rule", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Group.CapacityBelowActiveEnrollments");
        problem.GetProperty("args").GetProperty("count").GetInt32().ShouldBe(12);
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Problem_texts_follow_accept_language_while_the_code_stays()
    {
        using var client = ClientWithTestEndpoints();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));

        var turkish = await ProblemAsync(client, "/test/business-rule", acceptLanguage: null);
        turkish.Language.ShouldBe("tr");
        turkish.Body.GetProperty("code").GetString().ShouldBe("Group.CapacityBelowActiveEnrollments");
        turkish.Body.GetProperty("title").GetString().ShouldBe("İşlem yapılamadı");
        turkish.Body.GetProperty("detail").GetString()
            .ShouldBe("Kontenjan, gruptaki aktif öğrenci sayısının (12) altına düşürülemez.");

        var english = await ProblemAsync(client, "/test/business-rule", acceptLanguage: "en-GB,en;q=0.9,tr;q=0.8");
        english.Language.ShouldBe("en");
        english.Body.GetProperty("code").GetString().ShouldBe("Group.CapacityBelowActiveEnrollments");
        english.Body.GetProperty("args").GetProperty("count").GetInt32().ShouldBe(12);
        english.Body.GetProperty("title").GetString().ShouldBe("Request could not be completed");
        english.Body.GetProperty("detail").GetString()
            .ShouldBe("Capacity can’t be lower than the number of active students in the group (12).");
    }

    [Fact]
    public async Task Framework_problems_are_localized_too()
    {
        var anonymous = await ProblemAsync(Client, "/api/v1/anything", acceptLanguage: "en");
        anonymous.Body.GetProperty("code").GetString().ShouldBe("Auth.Unauthenticated");
        anonymous.Body.GetProperty("title").GetString().ShouldBe("Sign-in required");
        anonymous.Body.GetProperty("detail").GetString().ShouldBe("Sign in to continue.");

        using var client = ClientWithTestEndpoints();
        client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));
        var unexpected = await ProblemAsync(client, "/test/throw", acceptLanguage: "en");
        var traceId = unexpected.Body.GetProperty("traceId").GetString();
        unexpected.Body.GetProperty("detail").GetString()
            .ShouldBe($"Something went wrong. If the problem persists, send this code to the support team: {traceId}");

        var turkish = await ProblemAsync(client, "/test/throw", acceptLanguage: "de-DE");
        turkish.Body.GetProperty("title").GetString().ShouldBe("Beklenmeyen hata");
    }

    [Fact]
    public async Task Validation_problems_keep_field_codes_and_get_a_localized_detail()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "", password = "" }),
        };
        request.Headers.AcceptLanguage.ParseAdd("en");

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
        problem.GetProperty("detail").GetString().ShouldBe("Some fields in the form need to be corrected.");
        problem.GetProperty("errors").EnumerateObject().ShouldNotBeEmpty();
    }

    private static async Task<(JsonElement Body, string? Language)> ProblemAsync(HttpClient client, string path, string? acceptLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return (body, response.Content.Headers.ContentLanguage.SingleOrDefault());
    }

    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.BusinessRule, 422)]
    public void Error_types_map_to_documented_status_codes(ErrorType type, int status)
    {
        ProblemResults.StatusFor(type).ShouldBe(status);
    }

    private static string UnsignedToken(Guid user, Guid school, DateTimeOffset now)
    {
        static string Encode(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Encode($"{{\"sub\":\"{user}\",\"role\":\"SchoolAdmin\",\"tenant_id\":\"{school}\",\"iss\":\"spor-akademi\",\"aud\":\"spor-akademi-api\",\"exp\":{now.AddMinutes(15).ToUnixTimeSeconds()}}}");
        return $"{header}.{payload}.";
    }

    // The test endpoints sit behind the real pipeline (authentication + fallback policy), so they
    // also act as probes for token validation.
    private HttpClient ClientWithTestEndpoints() =>
        Api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>())).CreateClient();

    /// <summary>Appends throwaway endpoints after the real pipeline, so they run behind its error handling.</summary>
    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/test/throw", branch => branch.Run(_ => throw new InvalidOperationException("secret detail")));
            app.Map("/test/business-rule", branch => branch.Run(async context =>
            {
                var error = Error.BusinessRule("Group.CapacityBelowActiveEnrollments", "d").WithArgument("count", 12);
                await ProblemResults.Problem(error).ExecuteAsync(context);
            }));
        };
    }
}

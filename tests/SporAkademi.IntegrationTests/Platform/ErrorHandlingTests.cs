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
    public async Task Anonymous_calls_are_denied_by_default()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tokens_signed_with_another_key_are_rejected()
    {
        using var client = Api.CreateClient();
        var token = TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow());
        client.Authorize(token[..^4] + "AAAA");

        var response = await client.GetAsync(new Uri("/api/v1/anything", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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

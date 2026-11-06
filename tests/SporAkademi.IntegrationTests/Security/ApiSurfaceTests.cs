using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>
/// ASVS 13.2.1 and 13.2.5: routes accept only their methods and JSON bodies only as JSON. Without a token such a
/// request is refused even earlier (401, deny by default), so these run signed in.
/// </summary>
public sealed class ApiSurfaceTests(DatabaseFixture database) : IntegrationTest(database)
{
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Client.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", Guid.CreateVersion7(), Api.Clock.GetUtcNow()));
    }

    [Theory]
    [InlineData("DELETE", "/api/v1/auth/login")]
    [InlineData("PUT", "/api/v1/auth/logout")]
    [InlineData("GET", "/api/v1/auth/change-password")]
    [InlineData("DELETE", "/api/v1/students")]
    public async Task Undeclared_methods_are_refused(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Json_endpoints_refuse_other_content_types(string mediaType)
    {
        // A cross-site HTML form can only send these types; refusing them is part of the CSRF defence.
        using var content = new StringContent("""{"currentPassword":"Yanlis-Sifre-123","newPassword":"Yeni-Sifre-2027"}""", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var response = await Client.PostAsync(new Uri("/api/v1/auth/change-password", UriKind.Relative), content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }
}

using Microsoft.OpenApi;

namespace SporAkademi.Api.OpenApi;

/// <summary>
/// The OpenAPI document (P1-14) describes the contract the Angular client is generated from. It is only
/// served in Development and Testing; the committed copy in web/admin/openapi is kept current by a test.
/// </summary>
internal static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string Route = "/openapi/{documentName}.json";

    public static IServiceCollection AddApiDocument(this IServiceCollection services) =>
        services.AddOpenApi(DocumentName, options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo { Title = "Spor Akademi API", Version = DocumentName };
            document.Servers = [];
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token from POST /api/v1/auth/login (15 minutes).",
            };
            return Task.CompletedTask;
        }));

    public static WebApplication MapApiDocument(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            app.MapOpenApi(Route).AllowAnonymous();
        }

        return app;
    }
}

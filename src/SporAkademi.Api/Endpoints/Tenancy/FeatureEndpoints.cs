using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Tenancy;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Api.Endpoints.Tenancy;

internal sealed record SetSchoolFeatureRequest(bool Enabled);

/// <summary>
/// Feature flags per school (P4-10, DEC-580 – DEC-585): the Platform Admin lists and sets a school's flags; school
/// users and parents read their own school's enabled flags to show or hide menus and screens.
/// </summary>
internal static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/v1/platform/schools/{id:guid}/features").WithTags("Schools").RequireRole(Role.PlatformAdmin);
        platform.MapGet("/", ListSchoolFeatures).Produces<IReadOnlyList<SchoolFeatureDto>>().ProducesProblems(404);
        platform.MapPut("/{key}", SetSchoolFeature).Produces<SchoolFeatureDto>().ProducesProblems(400, 404, 409);

        app.MapGroup("/api/v1/features").WithTags("Features")
            .RequireRole(Role.SchoolAdmin, Role.Coach, Role.Parent)
            .MapGet("/", GetEnabledFeatures).Produces<EnabledFeaturesDto>().ProducesProblems();
        return app;
    }

    /// <summary>
    /// Hides the endpoint while <paramref name="feature"/> is off for the caller's school (DEC-584): the answer is the
    /// same <c>404 Resource.NotFound</c> as an unknown route, so it does not reveal that the endpoint exists. Runs after
    /// authentication and authorization, so a caller without access still gets 401/403 as before.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, FeatureDefinition feature)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(feature);
        return builder
            .WithMetadata(new RequiresFeatureMetadata(feature.Key))
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var flags = http.RequestServices.GetRequiredService<IFeatureFlags>();
                return await flags.IsEnabledAsync(feature.Key, http.RequestAborted)
                    ? await next(context)
                    : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
            });
    }

    private static async Task<IResult> ListSchoolFeatures(
        Guid id,
        IQueryHandler<ListSchoolFeaturesQuery, IReadOnlyList<SchoolFeatureDto>> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new ListSchoolFeaturesQuery(id), cancellationToken)).OkOrProblem();

    private static async Task<IResult> SetSchoolFeature(
        Guid id,
        string key,
        SetSchoolFeatureRequest request,
        ICommandHandler<SetSchoolFeatureCommand, Unit> handler,
        IQueryHandler<ListSchoolFeaturesQuery, IReadOnlyList<SchoolFeatureDto>> reader,
        CancellationToken cancellationToken)
    {
        var set = await handler.Handle(new SetSchoolFeatureCommand(id, key, request.Enabled), cancellationToken);
        if (set.IsFailure)
        {
            return ProblemResults.Problem(set.Error);
        }

        var features = await reader.Handle(new ListSchoolFeaturesQuery(id), cancellationToken);
        return features.IsSuccess
            ? TypedResults.Ok(features.Value.Single(f => f.Key == key))
            : ProblemResults.Problem(features.Error);
    }

    private static async Task<IResult> GetEnabledFeatures(
        IQueryHandler<GetEnabledFeaturesQuery, EnabledFeaturesDto> handler,
        CancellationToken cancellationToken) =>
        (await handler.Handle(new GetEnabledFeaturesQuery(), cancellationToken)).OkOrProblem();
}

/// <summary>Marks an endpoint gated by a feature flag (P4-10); read by the tests that check every gated route.</summary>
internal sealed record RequiresFeatureMetadata(string Key);

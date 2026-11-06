using Microsoft.AspNetCore.Mvc;
using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Api.Endpoints;

/// <summary>Shared endpoint plumbing: role gates, paging parameters, result mapping, documented error responses.</summary>
internal static class EndpointConventions
{
    public static RouteGroupBuilder RequireRole(this RouteGroupBuilder group, params Role[] roles)
    {
        var names = roles.Select(r => r.ToString()).ToArray();
        group.RequireAuthorization(policy => policy.RequireRole(names));
        return group;
    }

    /// <summary>Documents the problem responses every endpoint can return (ADR-0007).</summary>
    public static TBuilder ProducesProblems<TBuilder>(this TBuilder builder, params int[] statusCodes)
        where TBuilder : IEndpointConventionBuilder
    {
        foreach (var status in statusCodes.Append(StatusCodes.Status401Unauthorized).Append(StatusCodes.Status403Forbidden).Distinct())
        {
            builder.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ProblemDetails), ["application/problem+json"]));
        }

        return builder;
    }

    public static PageRequest ToPageRequest(int? page, int? pageSize) =>
        new(page ?? 1, pageSize ?? PageRequest.DefaultPageSize);

    public static IResult OkOrProblem<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : ProblemResults.Problem(result.Error);

    public static IResult NoContentOrProblem<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.NoContent() : ProblemResults.Problem(result.Error);
}

/// <summary>API page shape (appendix D §1.6).</summary>
internal sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages)
{
    public static PageResponse<T> From(PagedResult<T> page) =>
        new(page.Items, page.Page, page.PageSize, page.TotalCount, page.TotalPages);
}

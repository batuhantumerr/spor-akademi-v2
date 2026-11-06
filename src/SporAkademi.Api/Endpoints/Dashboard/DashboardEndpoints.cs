using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Dashboard;

namespace SporAkademi.Api.Endpoints.Dashboard;

/// <summary>The School Admin's home page in one request (DEC-210, NFR-PRF-01).</summary>
internal static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/dashboard").WithTags("Dashboard").RequireRole(Role.SchoolAdmin)
            .MapGet("/", GetDashboard).Produces<DashboardDto>().ProducesProblems();
        return app;
    }

    private static async Task<IResult> GetDashboard(IQueryHandler<GetDashboardQuery, DashboardDto> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetDashboardQuery(), cancellationToken)).OkOrProblem();
}

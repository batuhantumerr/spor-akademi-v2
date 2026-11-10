using SporAkademi.Api.Errors;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Application.Billing;

namespace SporAkademi.Api.Endpoints.Billing;

/// <summary>A fee rules version from <c>effectiveFrom</c> (<c>YYYY-MM</c>); percents are whole numbers from 0 to 100.</summary>
internal sealed record DefineBillingRulesRequest(
    string? EffectiveFrom,
    int? SecondChildPercent,
    int? ThirdAndLaterChildPercent,
    bool? SingleChargeOnGroupChange);

/// <summary>
/// P2-F8: the School Admin's fee rules (sibling discount, single charge on a group change; DEC-370). Versions act on the
/// caller's own school; there is no school id in the route or the body. Like group fees, a version is never edited or
/// deleted.
/// </summary>
internal static class BillingRulesEndpoints
{
    public static IEndpointRouteBuilder MapBillingRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/v1/settings/billing-rules").WithTags("Settings").RequireRole(Role.SchoolAdmin);
        rules.MapGet("/", ListRules).Produces<IReadOnlyList<BillingRulesDto>>();
        rules.MapPost("/", DefineRules).Produces<IReadOnlyList<BillingRulesDto>>(StatusCodes.Status201Created).ProducesProblems(400, 409, 422);
        return app;
    }

    private static async Task<IResult> ListRules(IQueryHandler<ListBillingRulesQuery, IReadOnlyList<BillingRulesDto>> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ListBillingRulesQuery(), cancellationToken)).OkOrProblem();

    /// <summary>Answers with every version, newest first (like <c>POST /groups/{id}/fees</c>), so the client sees which periods the new version covers.</summary>
    private static async Task<IResult> DefineRules(
        DefineBillingRulesRequest request,
        ICommandHandler<DefineBillingRulesCommand, Unit> handler,
        IQueryHandler<ListBillingRulesQuery, IReadOnlyList<BillingRulesDto>> reader,
        CancellationToken cancellationToken)
    {
        var defined = await handler.Handle(
            new DefineBillingRulesCommand(request.EffectiveFrom, request.SecondChildPercent, request.ThirdAndLaterChildPercent, request.SingleChargeOnGroupChange),
            cancellationToken);
        if (defined.IsFailure)
        {
            return ProblemResults.Problem(defined.Error);
        }

        var rules = await reader.Handle(new ListBillingRulesQuery(), cancellationToken);
        return TypedResults.Created("/api/v1/settings/billing-rules", rules.Value);
    }
}

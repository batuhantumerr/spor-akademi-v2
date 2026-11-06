using Microsoft.AspNetCore.Authorization;
using SporAkademi.Api.Errors;
using SporAkademi.Domain.Identity;

namespace SporAkademi.Api.Authentication;

/// <summary>Endpoint metadata: reachable with a <c>pwd_change_required</c> token (change password, profile, logout).</summary>
internal sealed class AllowedDuringPasswordChange
{
    public static readonly AllowedDuringPasswordChange Instance = new();
}

internal static class PasswordChangeRequiredExtensions
{
    public static TBuilder AllowDuringPasswordChange<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AllowedDuringPasswordChange.Instance);
}

/// <summary>
/// US-05: while the token says the password must change, every authenticated endpoint except the ones
/// marked <see cref="AllowedDuringPasswordChange"/> answers 403 <c>Auth.PasswordChangeRequired</c>.
/// Deny by default, so new endpoints are covered without opting in.
/// </summary>
internal sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is not null
            && context.User.Identity?.IsAuthenticated == true
            && string.Equals(context.User.FindFirst(JwtAccessTokenIssuer.PasswordChangeRequiredClaim)?.Value, "true", StringComparison.OrdinalIgnoreCase)
            && endpoint.Metadata.GetMetadata<AllowedDuringPasswordChange>() is null
            && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
        {
            await ProblemResults.Problem(AuthErrors.PasswordChangeRequired).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

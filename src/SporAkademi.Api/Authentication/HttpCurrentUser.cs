using System.Security.Claims;
using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Api.Authentication;

/// <summary>Reads the caller from the validated access token's claims (ADR-0006).</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string TenantIdClaim = "tenant_id";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => ReadGuid("sub") ?? ReadGuid(ClaimTypes.NameIdentifier);

    public Guid? TenantId => ReadGuid(TenantIdClaim);

    public Role? Role =>
        Enum.TryParse<Role>(Principal?.FindFirstValue(ClaimTypes.Role) ?? Principal?.FindFirstValue("role"), out var role)
            ? role
            : null;

    private Guid? ReadGuid(string claimType) =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(claimType), out var value) ? value : null;
}

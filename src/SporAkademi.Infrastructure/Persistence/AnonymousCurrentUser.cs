using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>Fallback for hosts without HTTP requests (tools, background work): no user.</summary>
internal sealed class AnonymousCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => false;

    public Guid? UserId => null;

    public Guid? TenantId => null;

    public Role? Role => null;
}

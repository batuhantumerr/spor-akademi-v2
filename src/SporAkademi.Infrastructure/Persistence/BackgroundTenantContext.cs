using SporAkademi.Application.Abstractions.Authentication;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// The actor of background work in one DI scope (outbox handlers now, scheduled jobs in P2-F2): no user, and the
/// school the work belongs to. Once entered, <see cref="ICurrentUser"/> in this scope reports it, so the tenant
/// query filters and the write guard (ADR-0003) apply exactly as in that school's requests. Enter it before
/// resolving anything that reads the current user (e.g. the DbContext's first query).
/// </summary>
internal sealed class BackgroundTenantContext
{
    public bool IsEntered { get; private set; }

    public Guid? TenantId { get; private set; }

    /// <param name="tenantId">The school; <c>null</c> for platform-level work (tenant filters then match no rows).</param>
    public void Enter(Guid? tenantId)
    {
        if (IsEntered)
        {
            throw new InvalidOperationException("The background tenant of a scope is set once; create a new scope per unit of work.");
        }

        IsEntered = true;
        TenantId = tenantId;
    }
}

/// <summary>Decorates the host's <see cref="ICurrentUser"/>: inside a background scope the background actor replaces it.</summary>
internal sealed class BackgroundAwareCurrentUser(ICurrentUser inner, BackgroundTenantContext background) : ICurrentUser
{
    public bool IsAuthenticated => !background.IsEntered && inner.IsAuthenticated;

    public Guid? UserId => background.IsEntered ? null : inner.UserId;

    public Guid? TenantId => background.IsEntered ? background.TenantId : inner.TenantId;

    public Role? Role => background.IsEntered ? null : inner.Role;
}

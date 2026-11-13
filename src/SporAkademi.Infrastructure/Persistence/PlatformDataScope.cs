namespace SporAkademi.Infrastructure.Persistence;

/// <summary>
/// P4-04 (DEC-526): marks a request scope whose database work is platform-level by design, whatever access token the
/// request happens to carry: the anonymous identity endpoints (login, refresh, logout, password links, DEC-105) and
/// the payment provider's endpoints. Once entered, <see cref="TenantSessionInterceptor"/> opens the session without a
/// school and with the platform flag, so a stale or foreign token cannot narrow a login's user search to its own
/// school. It changes nothing else: <c>ICurrentUser</c> still reports the token (e.g. a signed-in caller asking for a
/// reset link still gets none), and the platform flag still opens only the platform tables.
/// </summary>
public sealed class PlatformDataScope
{
    public bool IsEntered { get; private set; }

    public void Enter() => IsEntered = true;
}

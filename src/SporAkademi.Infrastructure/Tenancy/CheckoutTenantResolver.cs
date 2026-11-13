using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Billing;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

/// <summary>
/// DEC-344: the provider's anonymous webhook and callback carry no school. The school is read from the stored online
/// payment that owns the token (unique per provider), never from anything the caller sends; everything after this
/// runs in that school's scope with the normal tenant filters. This is a tenant-resolution concern, so it lives in
/// Tenancy (ADR-0003), and reads only the school and the id. Row level security (P4-04, DEC-523) hides every school's
/// payments from a scope without a school, so the lookup is the <c>billing.find_checkout_target</c> function, the
/// database's one narrow read across schools for this purpose.
/// </summary>
internal sealed class CheckoutTenantResolver(AppDbContext db) : ICheckoutTenantResolver
{
    public async Task<CheckoutTarget?> FindAsync(string provider, string token, CancellationToken cancellationToken)
    {
        var matches = await db.Database
            .SqlQuery<CheckoutRow>($"SELECT school_id, online_payment_id FROM billing.find_checkout_target({provider}, {token})")
            .ToListAsync(cancellationToken);
        return matches.SingleOrDefault() is { } match ? new CheckoutTarget(match.SchoolId, match.OnlinePaymentId) : null;
    }

    private sealed record CheckoutRow(Guid SchoolId, Guid OnlinePaymentId);
}

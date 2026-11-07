using Microsoft.EntityFrameworkCore;
using SporAkademi.Application.Billing;
using SporAkademi.Domain.Billing;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Tenancy;

/// <summary>
/// DEC-344: the provider's anonymous webhook and callback carry no school. The school is read from the stored online
/// payment that owns the token (unique per provider), never from anything the caller sends; everything after this
/// runs in that school's scope with the normal tenant filters. This is a tenant-resolution concern, so it lives in
/// Tenancy, the only place allowed to bypass the filters (ADR-0003), and reads only the school and the id.
/// </summary>
internal sealed class CheckoutTenantResolver(AppDbContext db) : ICheckoutTenantResolver
{
    public async Task<CheckoutTarget?> FindAsync(string provider, string token, CancellationToken cancellationToken)
    {
        var match = await db.Set<OnlinePayment>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Provider == provider && p.ProviderToken == token)
            .Select(p => new { p.TenantId, p.Id })
            .SingleOrDefaultAsync(cancellationToken);
        return match is null ? null : new CheckoutTarget(match.TenantId.Value, match.Id.Value);
    }
}

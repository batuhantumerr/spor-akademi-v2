using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Billing;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>A fresh DI scope acting for one school, exactly like an outbox handler's scope (DEC-302, DEC-344).</summary>
internal sealed class TenantWorkScope(IServiceScopeFactory scopeFactory) : ITenantWorkScope
{
    public async Task<T> RunAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A school is required.", nameof(tenantId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(tenantId);
        return await work(scope.ServiceProvider);
    }
}

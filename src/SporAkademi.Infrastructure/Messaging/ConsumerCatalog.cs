using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.Infrastructure.Messaging;

/// <summary>One handler of one message type: it gets its own queue, so handlers fail, retry and scale independently.</summary>
internal sealed record ConsumerRegistration(OutboxMessageType Type, Type HandlerType)
{
    /// <summary>The first segment of the message type (<c>billing</c>): the module the consumer belongs to.</summary>
    public string Module => Type.Name[..Type.Name.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>Also the inbox's consumer key.</summary>
    public string Queue => $"{Type.Name}.{HandlerType.Name}";

    public string RetryQueue => Queue + ".retry";
}

/// <summary>
/// The consumers of this process image: every <see cref="IOutboxMessageHandler{TMessage}"/> registered for a known message
/// type, found once through the container (so test hosts and later modules register handlers the usual way). The publisher
/// declares every queue too, so a message is never published into an exchange without its queues, whichever host consumes.
/// </summary>
internal sealed class ConsumerCatalog(OutboxMessageTypes types, IServiceScopeFactory scopeFactory)
{
    private readonly Lazy<IReadOnlyList<ConsumerRegistration>> _all = new(() => Discover(types, scopeFactory));

    public IReadOnlyList<ConsumerRegistration> All => _all.Value;

    private static List<ConsumerRegistration> Discover(OutboxMessageTypes types, IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        // A background scope without a school: constructing a handler never reads a request's user.
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(null);
        var result = new List<ConsumerRegistration>();
        foreach (var type in types.All.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var handlerInterface = typeof(IOutboxMessageHandler<>).MakeGenericType(type.ClrType);
            foreach (var handler in scope.ServiceProvider.GetServices(handlerInterface).Select(h => h!.GetType()).Distinct())
            {
                result.Add(new ConsumerRegistration(type, handler));
            }
        }

        return result;
    }
}

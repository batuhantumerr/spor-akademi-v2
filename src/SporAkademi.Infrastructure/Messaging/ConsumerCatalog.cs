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

    /// <summary>The handler's <see cref="OutboxConsumerGroupAttribute"/> group (P5-04), or <c>null</c>.</summary>
    public string? Group => HandlerType.GetCustomAttributes(typeof(OutboxConsumerGroupAttribute), inherit: false)
        .Cast<OutboxConsumerGroupAttribute>().SingleOrDefault()?.Group;
}

/// <summary>
/// The consumers of this process image: every <see cref="IOutboxMessageHandler{TMessage}"/> registered for a known message
/// type, found once from the service registrations (so test hosts and later modules register handlers the usual way). The
/// registrations are read, not resolved: a worker that never runs a handler (the notifications worker has no document store
/// for the handler that deletes documents) must not have to construct it. A registration made by a factory is the one case
/// that needs an instance to learn its type. The publisher declares every queue too, so a message is never published into an
/// exchange without its queues, whichever host consumes.
/// </summary>
internal sealed class ConsumerCatalog(OutboxMessageTypes types, IServiceScopeFactory scopeFactory, IServiceCollection registrations)
{
    private readonly Lazy<IReadOnlyList<ConsumerRegistration>> _all = new(() => Discover(types, scopeFactory, registrations));

    public IReadOnlyList<ConsumerRegistration> All => _all.Value;

    private static List<ConsumerRegistration> Discover(OutboxMessageTypes types, IServiceScopeFactory scopeFactory, IServiceCollection registrations)
    {
        var result = new List<ConsumerRegistration>();
        foreach (var type in types.All.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var handlerInterface = typeof(IOutboxMessageHandler<>).MakeGenericType(type.ClrType);
            var handlers = registrations.Where(d => d.ServiceType == handlerInterface).Select(d => HandlerTypeOf(d, handlerInterface, scopeFactory)).Distinct();
            foreach (var handler in handlers)
            {
                result.Add(new ConsumerRegistration(type, handler));
            }
        }

        return result;
    }

    private static Type HandlerTypeOf(ServiceDescriptor descriptor, Type handlerInterface, IServiceScopeFactory scopeFactory)
    {
        if (descriptor.IsKeyedService)
        {
            throw new InvalidOperationException($"Keyed outbox handlers are not supported ({handlerInterface.Name}).");
        }

        if (descriptor.ImplementationType is { } implementation)
        {
            return implementation;
        }

        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance.GetType();
        }

        // A factory: the type is known only from an instance, built in a background scope without a school.
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(null);
        return descriptor.ImplementationFactory!(scope.ServiceProvider).GetType();
    }
}

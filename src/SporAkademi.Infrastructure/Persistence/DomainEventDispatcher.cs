using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Messaging;
using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Infrastructure.Persistence;

/// <summary>Resolves every <see cref="IDomainEventConsumer{TEvent}"/> for an event's runtime type and runs them in order.</summary>
internal sealed class DomainEventDispatcher(IServiceProvider serviceProvider)
{
    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IDomainEvent, CancellationToken, Task<Result>>> Invokers = new();

    public Task<Result> DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var invoker = Invokers.GetOrAdd(domainEvent.GetType(), CreateInvoker);
        return invoker(serviceProvider, domainEvent, cancellationToken);
    }

    private static Func<IServiceProvider, IDomainEvent, CancellationToken, Task<Result>> CreateInvoker(Type eventType)
    {
        var method = typeof(DomainEventDispatcher)
            .GetMethod(nameof(InvokeHandlersAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(eventType);
        return method.CreateDelegate<Func<IServiceProvider, IDomainEvent, CancellationToken, Task<Result>>>();
    }

    private static async Task<Result> InvokeHandlersAsync<TEvent>(
        IServiceProvider provider,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        foreach (var handler in provider.GetServices<IDomainEventConsumer<TEvent>>())
        {
            var result = await handler.Handle((TEvent)domainEvent, cancellationToken);
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Result.Success();
    }
}

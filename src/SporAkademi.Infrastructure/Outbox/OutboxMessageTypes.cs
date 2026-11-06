using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Outbox;

namespace SporAkademi.Infrastructure.Outbox;

/// <summary>One registered message type; registered per type so tests and later modules can add their own.</summary>
internal sealed record OutboxMessageRegistration(Type ClrType);

/// <summary>
/// The type registry (DEC-301): stored names map to CLR types in both directions. A name read from the database is
/// only ever looked up here, never loaded as a type, so a row cannot make the processor instantiate an arbitrary type.
/// </summary>
internal sealed partial class OutboxMessageTypes
{
    private readonly Dictionary<string, OutboxMessageType> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, OutboxMessageType> _byType = [];

    public OutboxMessageTypes(IEnumerable<OutboxMessageRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        foreach (var clrType in registrations.Select(r => r.ClrType).Distinct())
        {
            var type = OutboxMessageType.Create(clrType);
            if (!_byName.TryAdd(type.Name, type))
            {
                throw new InvalidOperationException($"Outbox message name '{type.Name}' is used by {_byName[type.Name].ClrType.Name} and {clrType.Name}.");
            }

            _byType.Add(clrType, type);
        }
    }

    public IReadOnlyCollection<OutboxMessageType> All => _byName.Values;

    public OutboxMessageType Get(Type clrType) =>
        _byType.TryGetValue(clrType, out var type)
            ? type
            : throw new InvalidOperationException($"{clrType.Name} is not a registered outbox message.");

    public bool TryGet(string name, out OutboxMessageType type) => _byName.TryGetValue(name, out type!);

    internal static string NameOf(Type clrType) =>
        clrType.GetCustomAttribute<OutboxMessageAttribute>()?.Name
            ?? throw new InvalidOperationException($"{clrType.Name} has no [OutboxMessage] name.");

    /// <summary><c>module.event-name.vN</c>: lower case, dot separated, explicitly versioned.</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+\.v[1-9][0-9]*$")]
    internal static partial Regex NamePattern();
}

/// <summary>A registered message type: its stored name, its serializer and the call into its handlers.</summary>
internal sealed class OutboxMessageType
{
    /// <summary>Web defaults (camelCase), enums by name; a payload missing a constructor parameter is rejected, not defaulted.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly MethodInfo InvokeMethod =
        typeof(OutboxMessageType).GetMethod(nameof(InvokeHandlersAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Func<IServiceProvider, object, OutboxMessageContext, CancellationToken, Task> _invoke;

    private OutboxMessageType(string name, Type clrType)
    {
        Name = name;
        ClrType = clrType;
        _invoke = InvokeMethod.MakeGenericMethod(clrType)
            .CreateDelegate<Func<IServiceProvider, object, OutboxMessageContext, CancellationToken, Task>>();
    }

    public string Name { get; }

    public Type ClrType { get; }

    public static OutboxMessageType Create(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        if (!typeof(IOutboxMessage).IsAssignableFrom(clrType) || clrType.IsAbstract || clrType.IsGenericTypeDefinition)
        {
            throw new InvalidOperationException($"{clrType.Name} is not a concrete outbox message.");
        }

        var name = OutboxMessageTypes.NameOf(clrType);
        if (name.Length > OutboxMessage.TypeMaxLength || !OutboxMessageTypes.NamePattern().IsMatch(name))
        {
            throw new InvalidOperationException($"Outbox message name '{name}' of {clrType.Name} must look like 'module.event-name.v1'.");
        }

        return new OutboxMessageType(name, clrType);
    }

    public string Serialize(object message) => JsonSerializer.Serialize(message, ClrType, JsonOptions);

    /// <exception cref="JsonException">The payload does not match the message's shape.</exception>
    public object Deserialize(string payload) =>
        JsonSerializer.Deserialize(payload, ClrType, JsonOptions) ?? throw new JsonException("The payload is null.");

    /// <summary>Runs every handler of the message in order; the first exception fails the delivery.</summary>
    public Task InvokeAsync(IServiceProvider services, object message, OutboxMessageContext context, CancellationToken cancellationToken) =>
        _invoke(services, message, context, cancellationToken);

    private static async Task InvokeHandlersAsync<TMessage>(
        IServiceProvider services,
        object message,
        OutboxMessageContext context,
        CancellationToken cancellationToken)
        where TMessage : IOutboxMessage
    {
        foreach (var handler in services.GetServices<IOutboxMessageHandler<TMessage>>())
        {
            await handler.Handle((TMessage)message, context, cancellationToken);
        }
    }
}

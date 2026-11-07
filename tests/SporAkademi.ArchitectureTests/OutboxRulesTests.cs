using System.Reflection;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Infrastructure.Outbox;

namespace SporAkademi.ArchitectureTests;

/// <summary>P2-F3 (DEC-301): outbox messages are stored contracts, so their shape is restricted.</summary>
public sealed class OutboxRulesTests
{
    /// <summary>Values that serialize stably; domain types (ids, value objects) would tie stored JSON to domain refactorings.</summary>
    private static readonly Type[] AllowedPropertyTypes =
    [
        typeof(Guid), typeof(string), typeof(bool), typeof(int), typeof(long), typeof(decimal),
        typeof(DateOnly), typeof(TimeOnly), typeof(DateTimeOffset),
    ];

    private static readonly List<Type> Messages =
    [
        .. Assemblies.Application.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IOutboxMessage).IsAssignableFrom(t)),
    ];

    [Fact]
    public void Messages_are_public_sealed_records_in_a_module_contracts_namespace()
    {
        Messages.ShouldNotBeEmpty();
        var offenders = Messages
            .Where(t => !t.IsPublic || !t.IsSealed || t.GetMethod("<Clone>$") is null
                || t.Namespace?.EndsWith(".Contracts", StringComparison.Ordinal) != true)
            .Select(t => t.FullName)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Messages_have_unique_versioned_names()
    {
        // The registry rejects bad and duplicate names at startup; this fails the build instead.
        var types = new OutboxMessageTypes(Messages.Select(t => new OutboxMessageRegistration(t)));

        types.All.Count.ShouldBe(Messages.Count);
    }

    [Fact]
    public void Messages_carry_only_primitive_values()
    {
        var offenders = Messages
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => !IsAllowed(x.Property.PropertyType))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static bool IsAllowed(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return AllowedPropertyTypes.Contains(underlying) || underlying.IsEnum;
    }
}

using System.Reflection;
using NetArchTest.Rules;

namespace SporAkademi.ArchitectureTests;

/// <summary>
/// DEC-312: tenant data reaches the cache only through <c>ITenantCache</c> and its tenant-bound keys. The cache
/// libraries themselves are used in one place, <c>SporAkademi.Infrastructure.Caching</c>.
/// </summary>
[Trait("Story", "US-02")]
public sealed class CachingRulesTests
{
    private const string CachingNamespace = "SporAkademi.Infrastructure.Caching";

    private static readonly string[] CacheLibraries =
        ["Microsoft.Extensions.Caching.Hybrid", "Microsoft.Extensions.Caching.Distributed", "Microsoft.Extensions.Caching.StackExchangeRedis", "StackExchange.Redis"];

    public static TheoryData<string> AssemblyNames() => ["Application", "Infrastructure", "Api"];

    [Theory]
    [MemberData(nameof(AssemblyNames))]
    public void Only_the_caching_namespace_uses_the_cache_libraries(string assembly)
    {
        var result = Types.InAssembly(Pick(assembly))
            .That().DoNotResideInNamespace(CachingNamespace)
            .ShouldNot().HaveDependencyOnAny(CacheLibraries)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Memory_cache_is_not_used_for_tenant_data_either()
    {
        // IMemoryCache has no tenant-bound keys or tag invalidation; ITenantCache already keeps an in-memory L1.
        var result = Types.InAssembly(Assemblies.Infrastructure)
            .ShouldNot().HaveDependencyOn("Microsoft.Extensions.Caching.Memory")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    private static Assembly Pick(string name) => name switch
    {
        "Application" => Assemblies.Application,
        "Infrastructure" => Assemblies.Infrastructure,
        "Api" => Assemblies.Api,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}

using System.Reflection;

namespace SporAkademi.ArchitectureTests;

/// <summary>DEC-220: demo data code never ships in an image, because no deployable assembly references the seeder.</summary>
public sealed class DemoSeedIsolationTests
{
    private const string DemoSeed = "SporAkademi.DemoSeed";

    public static TheoryData<string> DeployableAssemblies() =>
        [Assemblies.Api.GetName().Name!, Assemblies.Application.GetName().Name!, Assemblies.Domain.GetName().Name!, Assemblies.Infrastructure.GetName().Name!];

    [Theory]
    [MemberData(nameof(DeployableAssemblies))]
    public void Deployable_assemblies_never_reference_the_demo_seeder(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        assembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain(DemoSeed);
    }

    [Fact]
    public void The_api_build_output_has_no_demo_seeder()
    {
        // The API's own dependency closure (what dotnet publish copies) must not contain the seeder.
        var apiDirectory = Path.GetDirectoryName(Assemblies.Api.Location)!;
        var deps = File.ReadAllText(Path.Combine(apiDirectory, "SporAkademi.Api.deps.json"));

        deps.ShouldNotContain(DemoSeed);
    }
}

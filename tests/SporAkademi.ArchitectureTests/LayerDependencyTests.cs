using NetArchTest.Rules;

namespace SporAkademi.ArchitectureTests;

public sealed class LayerDependencyTests
{
    private const string Domain = "SporAkademi.Domain";
    private const string Application = "SporAkademi.Application";
    private const string Infrastructure = "SporAkademi.Infrastructure";
    private const string Api = "SporAkademi.Api";

    [Fact]
    public void Domain_does_not_depend_on_other_layers()
    {
        var result = Types.InAssembly(Assemblies.Domain)
            .ShouldNot().HaveDependencyOnAny(Application, Infrastructure, Api)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(Assemblies.Application)
            .ShouldNot().HaveDependencyOnAny(Infrastructure, Api)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_api()
    {
        var result = Types.InAssembly(Assemblies.Infrastructure)
            .ShouldNot().HaveDependencyOn(Api)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }
}

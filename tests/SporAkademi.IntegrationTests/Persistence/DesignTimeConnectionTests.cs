using SporAkademi.Infrastructure.Persistence;

namespace SporAkademi.IntegrationTests.Persistence;

/// <summary>
/// P5-06 (DEC-653): the migrator image's bundle takes its connection from the environment, so a container platform can fill
/// it from a secret: <c>SPOR_AKADEMI_DESIGN_CONNECTION</c> (scripts/ef.sh) or <c>ConnectionStrings__Migrator</c> (documented, cloud).
/// </summary>
public sealed class DesignTimeConnectionTests
{
    private static string Resolve(Dictionary<string, string?> environment) =>
        DesignTimeDbContextFactory.ResolveConnection(name => environment.GetValueOrDefault(name));

    [Fact]
    [Trait("Story", "P5-06")]
    public void The_migrator_connection_string_variable_is_read()
    {
        Resolve(new() { ["ConnectionStrings__Migrator"] = "Host=db;Database=x" }).ShouldBe("Host=db;Database=x");
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void The_design_time_variable_of_ef_sh_wins_when_both_are_set()
    {
        Resolve(new() { ["SPOR_AKADEMI_DESIGN_CONNECTION"] = "Host=local", ["ConnectionStrings__Migrator"] = "Host=other" }).ShouldBe("Host=local");
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void Without_any_variable_a_placeholder_serves_commands_that_do_not_connect()
    {
        Resolve([]).ShouldContain("spor_akademi_design_time");
        Resolve(new() { ["ConnectionStrings__Migrator"] = "  " }).ShouldContain("spor_akademi_design_time");
    }
}

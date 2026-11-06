using System.Reflection;

namespace SporAkademi.ArchitectureTests;

internal static class Assemblies
{
    public static readonly Assembly Domain = Assembly.Load("SporAkademi.Domain");
    public static readonly Assembly Application = Assembly.Load("SporAkademi.Application");
    public static readonly Assembly Infrastructure = typeof(SporAkademi.Infrastructure.DependencyInjection).Assembly;
    public static readonly Assembly Api = typeof(Program).Assembly;
}

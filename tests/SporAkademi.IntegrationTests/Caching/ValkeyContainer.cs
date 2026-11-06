using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace SporAkademi.IntegrationTests.Caching;

/// <summary>
/// A Valkey server like the docker compose one (DEC-310): image (tag + digest) read from compose.yaml, password
/// required, nothing on disk. A generic Testcontainers container, so no extra test package is needed.
/// </summary>
public sealed partial class ValkeyContainer : IAsyncDisposable
{
    private const int Port = 6379;
    private const string Password = "valkeyTestPassword1";

    private readonly IContainer _container = new ContainerBuilder(ReadValkeyImage())
        .WithCommand("valkey-server", "--requirepass", Password, "--save", "", "--appendonly", "no")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public string ConnectionString => $"{_container.Hostname}:{_container.GetMappedPublicPort(Port)},password={Password}";

    public Task StartAsync(CancellationToken cancellationToken = default) => _container.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) => _container.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>compose.yaml is the single source of truth for the Valkey image, like for PostgreSQL.</summary>
    private static string ReadValkeyImage()
    {
        var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "compose.yaml"));
        var match = ValkeyImage().Match(compose);
        return match.Success
            ? match.Groups["image"].Value
            : throw new InvalidOperationException("compose.yaml has no valkey image.");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>valkey/valkey:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ValkeyImage();
}

/// <summary>One Valkey server per test class that needs it.</summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    public ValkeyContainer Valkey { get; } = new();

    public async ValueTask InitializeAsync() => await Valkey.StartAsync();

    public ValueTask DisposeAsync() => Valkey.DisposeAsync();
}

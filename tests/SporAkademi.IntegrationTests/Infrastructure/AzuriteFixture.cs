using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using SporAkademi.Infrastructure.Storage;
using Testcontainers.Azurite;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// The Azure Blob emulator for tests (P5-06, DEC-650): the Azurite image of compose.yaml, started once per test class.
/// TEST ONLY: Azurite cannot hand out user delegation keys to a managed identity, so tests reach it through its development
/// account key and sign links with that key (<see cref="SharedKeySigner"/>). Production code has no such path: it never
/// reads an account key or a connection string, and the real storage account has shared keys switched off.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string ContainerName = "documents";

    private readonly AzuriteContainer _container = new AzuriteBuilder(DatabaseFixture.ReadImage(DatabaseFixture.AzuriteImage()))
        .WithCommand("--skipApiVersionCheck", "--disableProductStyleUrl")
        .Build();

    /// <summary>A real clock for links: the emulator checks signatures against real time, like the cloud.</summary>
    internal static StorageClock Clock { get; } = new(TimeProvider.System);

    public BlobServiceClient Service { get; private set; } = null!;

    public string BlobEndpoint => _container.GetBlobEndpoint();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The container's own connection string carries the emulator's well-known development account; nothing is spelled out here.
        Service = new BlobServiceClient(_container.GetConnectionString());
        await Service.GetBlobContainerClient(ContainerName).CreateIfNotExistsAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>The storage the API would build, with the emulator's account key in place of a user delegation key.</summary>
    internal AzureBlobDocumentStorage CreateStorage(TimeSpan? linkLifetime = null, string container = ContainerName) =>
        new(CreateClients(linkLifetime, container), Clock);

    internal AzureBlobClients CreateClients(TimeSpan? linkLifetime = null, string container = ContainerName)
    {
        var options = new DocumentStorageOptions
        {
            Provider = DocumentStorageProvider.AzureBlob,
            BlobServiceUrl = BlobEndpoint,
            Container = container,
            DownloadLinkLifetime = linkLifetime ?? TimeSpan.FromMinutes(5),
        };
        return new AzureBlobClients(Service, options, new SharedKeySigner(Credential()));
    }

    private StorageSharedKeyCredential Credential()
    {
        var parts = _container.GetConnectionString().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return new StorageSharedKeyCredential(parts["AccountName"], parts["AccountKey"]);
    }

    /// <summary>Test-only signer: the emulator's development account key instead of a user delegation key.</summary>
    private sealed class SharedKeySigner(StorageSharedKeyCredential credential) : IBlobSasSigner
    {
        public string Sign(BlobSasBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.ToSasQueryParameters(credential).ToString();
        }
    }
}

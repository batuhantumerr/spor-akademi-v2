using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Storage;

namespace SporAkademi.Infrastructure.Storage;

/// <summary>Signs the query string of a blob SAS link. Production signs with a user delegation key; tests with Azurite's account key.</summary>
internal interface IBlobSasSigner
{
    string Sign(BlobSasBuilder builder);
}

/// <summary>
/// Signs with a user delegation key obtained through Entra ID (P5-06, DEC-650): the storage account has shared keys
/// switched off (P5-05), so no account key exists to sign with. The key is fetched once and reused until it is close to
/// its end; a link never outlives the key it was signed with.
/// </summary>
internal sealed class UserDelegationSasSigner(BlobServiceClient service, StorageClock clock) : IBlobSasSigner
{
    /// <summary>How long a delegation key lives (the service allows up to 7 days) and when it is replaced.</summary>
    internal static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private UserDelegationKey? _key;

    public string Sign(BlobSasBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ToSasQueryParameters(CurrentKey(), service.AccountName).ToString();
    }

    private UserDelegationKey CurrentKey()
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            if (_key is null || _key.SignedExpiresOn - now < RenewBefore)
            {
                // Starts a little in the past so a slightly slow storage clock does not refuse the first links.
                _key = service.GetUserDelegationKey(new BlobGetUserDelegationKeyOptions(now + KeyLifetime) { StartsOn = now.AddMinutes(-5) }).Value;
            }

            return _key;
        }
    }
}

/// <summary>The blob endpoint and container; one client each, thread-safe.</summary>
internal sealed class AzureBlobClients(BlobServiceClient service, DocumentStorageOptions options, IBlobSasSigner signer)
{
    public BlobContainerClient Container { get; } = service.GetBlobContainerClient(options.Container);

    public DocumentStorageOptions Options { get; } = options;

    public IBlobSasSigner Signer { get; } = signer;

    /// <summary>Managed identity only: <c>ManagedIdentityCredential</c> for a client id, else <c>DefaultAzureCredential</c>. No key or connection string.</summary>
    public static AzureBlobClients Create(IOptions<DocumentStorageOptions> settings, StorageClock clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.Value;
        TokenCredential credential = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        var service = new BlobServiceClient(options.AzureEndpoint, credential);
        return new AzureBlobClients(service, options, new UserDelegationSasSigner(service, clock));
    }
}

/// <summary>
/// <see cref="IDocumentStorage"/> on Azure Blob Storage (P5-06, DEC-650). Same contract as the S3 adapter: private blobs, reading
/// only through short-lived links signed here (a user delegation SAS) that fix the served content type, disposition and cache control.
/// </summary>
internal sealed class AzureBlobDocumentStorage(AzureBlobClients clients, StorageClock clock) : IDocumentStorage
{
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        // Blob Storage encrypts everything at rest (Microsoft-managed keys) with no request header; nothing to ask for.
        await clients.Container.GetBlobClient(key).UploadAsync(
            stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await clients.Container.GetBlobClient(key).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);

    public DownloadLink CreateDownloadLink(string key, DownloadDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var expiresAt = clock.UtcNow + clients.Options.DownloadLinkLifetime;
        var blob = clients.Container.GetBlobClient(key);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresAt)
        {
            BlobContainerName = clients.Container.Name,
            BlobName = key,
            Resource = "b",
            Protocol = blob.Uri.Scheme == Uri.UriSchemeHttp ? SasProtocol.HttpsAndHttp : SasProtocol.Https,
            ContentType = disposition.ContentType,
            ContentDisposition = DownloadHeaders.ContentDisposition(disposition),
            CacheControl = DownloadHeaders.CacheControl,
        };
        return new DownloadLink(new UriBuilder(blob.Uri) { Query = clients.Signer.Sign(sas) }.Uri, expiresAt);
    }
}

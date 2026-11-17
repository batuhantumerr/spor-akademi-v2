using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Storage;

namespace SporAkademi.Infrastructure.Storage;

/// <summary>Which store holds the documents (P5-06, DEC-650). S3 is the default and stays the local development store.</summary>
public enum DocumentStorageProvider
{
    S3,
    AzureBlob,
}

/// <summary>
/// Settings of the document store (section <c>Storage</c>, DEC-320, DEC-650). <see cref="Provider"/> picks the S3-compatible
/// store or Azure Blob Storage; each has its own keys. Read and validated on first use, so the API starts without them;
/// the <c>storage</c> readiness check then reports "Degraded" (DEC-326).
/// </summary>
public sealed class DocumentStorageOptions
{
    public const string Section = "Storage";

    public DocumentStorageProvider Provider { get; set; } = DocumentStorageProvider.S3;

    /// <summary>Azure Blob: the storage account, e.g. <c>stsporproddocs</c>. The endpoint is <c>https://{AccountName}.blob.core.windows.net</c>.</summary>
    public string? AccountName { get; set; }

    /// <summary>Azure Blob: the endpoint when it is not the public cloud's (a sovereign cloud, or the Azurite emulator).</summary>
    public string? BlobServiceUrl { get; set; }

    /// <summary>Azure Blob: the container holding the documents (private).</summary>
    public string? Container { get; set; }

    /// <summary>
    /// Azure Blob: client id of the user-assigned managed identity. Empty uses <c>DefaultAzureCredential</c> (the system-assigned
    /// identity, a developer's <c>az login</c>); set, only <c>ManagedIdentityCredential</c> is tried. No account key is ever read.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    internal Uri AzureEndpoint => Uri.TryCreate(BlobServiceUrl, UriKind.Absolute, out var url)
        ? url
        : new Uri($"https://{AccountName}.blob.core.windows.net");

    internal bool IsValidForProvider(out string error)
    {
        error = "";
        if (DownloadLinkLifetime < TimeSpan.FromSeconds(1) || DownloadLinkLifetime > TimeSpan.FromMinutes(15))
        {
            error = "Storage:DownloadLinkLifetime must be between 1 second and 15 minutes.";
        }
        else if (Provider == DocumentStorageProvider.S3)
        {
            error = !IsAbsoluteHttpUrl(ServiceUrl) ? "Storage:ServiceUrl must be an http(s) URL."
                : PublicUrl is not null && !IsAbsoluteHttpUrl(PublicUrl) ? "Storage:PublicUrl must be an http(s) URL."
                : string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey) ? "Storage:AccessKey and Storage:SecretKey are required."
                : string.IsNullOrWhiteSpace(Bucket) ? "Storage:Bucket is required."
                : "";
        }
        else
        {
            error = BlobServiceUrl is not null && !IsAbsoluteHttpUrl(BlobServiceUrl) ? "Storage:BlobServiceUrl must be an http(s) URL."
                : string.IsNullOrWhiteSpace(BlobServiceUrl) && string.IsNullOrWhiteSpace(AccountName) ? "Storage:AccountName (or Storage:BlobServiceUrl) is required."
                : string.IsNullOrWhiteSpace(Container) ? "Storage:Container is required."
                : "";
        }

        return error.Length == 0;
    }

    /// <summary>The endpoint the API talks to, e.g. <c>http://127.0.0.1:58333</c>.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Base of signed links when browsers reach the store under another address; defaults to <see cref="ServiceUrl"/>.</summary>
    public string? PublicUrl { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string? Bucket { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>How long a download link works (DEC-323): 5 minutes.</summary>
    public TimeSpan DownloadLinkLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Ask the store to encrypt objects at rest (SSE-S3). On by default; health reports are special category data.</summary>
    public bool ServerSideEncryption { get; set; } = true;

    internal static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// Names the wrong setting in the error the first use of the store throws (and the readiness check reports). Azure Blob
/// must be reached over https: the Azure SDK refuses to send a bearer token over plain http anyway, so an http endpoint
/// could only ever fail at the first request. Tests reach the Azurite emulator with its own client, not through this setting.
/// </summary>
internal sealed class DocumentStorageOptionsValidator : IValidateOptions<DocumentStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, DocumentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValidForProvider(out var error))
        {
            return ValidateOptionsResult.Fail(error);
        }

        return options.Provider == DocumentStorageProvider.AzureBlob && options.AzureEndpoint.Scheme != Uri.UriSchemeHttps
            ? ValidateOptionsResult.Fail("Storage:BlobServiceUrl must be https (a managed identity token is never sent over http).")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Fail fast (P5-06, DEC-650): when any <c>Storage</c> setting is present the host is meant to have a store, so a typo or a
/// missing account name stops the start instead of leaving readiness "Degraded". With no <c>Storage</c> setting at all the
/// host still starts (DEC-326: CI smoke test, demo seeder) and only first use reports it.
/// </summary>
internal sealed class DocumentStorageStartupCheck(IConfiguration configuration, IOptions<DocumentStorageOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetSection(DocumentStorageOptions.Section).GetChildren().Any())
        {
            _ = options.Value; // throws OptionsValidationException naming the setting
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The real clock used for link expiry: signatures are checked by the store against real time, not the app's clock.</summary>
internal sealed class StorageClock(TimeProvider clock)
{
    public DateTimeOffset UtcNow => clock.GetUtcNow();
}

/// <summary>One client for requests and one for signing links with the public address; both are thread-safe.</summary>
internal sealed class S3Clients : IDisposable
{
    public S3Clients(IOptions<DocumentStorageOptions> options)
    {
        Options = options.Value;
        var credentials = new BasicAWSCredentials(Options.AccessKey, Options.SecretKey);
        Requests = new AmazonS3Client(credentials, Config(Options.ServiceUrl!));
        Signing = new AmazonS3Client(credentials, Config(Options.PublicUrl ?? Options.ServiceUrl!));
        PublicIsHttp = new Uri(Options.PublicUrl ?? Options.ServiceUrl!).Scheme == Uri.UriSchemeHttp;
    }

    public DocumentStorageOptions Options { get; }

    public IAmazonS3 Requests { get; }

    public IAmazonS3 Signing { get; }

    public bool PublicIsHttp { get; }

    public void Dispose()
    {
        Requests.Dispose();
        Signing.Dispose();
    }

    private AmazonS3Config Config(string serviceUrl) => new()
    {
        ServiceURL = serviceUrl,
        ForcePathStyle = true,
        AuthenticationRegion = Options.Region,
        // S3-compatible stores do not all support the SDK's default trailing checksums.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromSeconds(30),
        MaxErrorRetry = 2,
    };
}

/// <summary>
/// <see cref="IDocumentStorage"/> on any S3-compatible store (SeaweedFS locally and in tests, DEC-320). Objects are
/// private; reading is only possible through links signed here, which also fix the served content type and
/// disposition, so a stored file is never interpreted as anything else by the browser.
/// </summary>
internal sealed class S3DocumentStorage(S3Clients clients, StorageClock clock) : IDocumentStorage
{
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = clients.Options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        };
        if (clients.Options.ServerSideEncryption)
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await clients.Requests.PutObjectAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await clients.Requests.DeleteObjectAsync(clients.Options.Bucket, key, cancellationToken);

    public DownloadLink CreateDownloadLink(string key, DownloadDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var expiresAt = clock.UtcNow + clients.Options.DownloadLinkLifetime;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = clients.Options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = clients.PublicIsHttp ? Protocol.HTTP : Protocol.HTTPS,
        };
        request.ResponseHeaderOverrides.ContentType = disposition.ContentType;
        request.ResponseHeaderOverrides.ContentDisposition = DownloadHeaders.ContentDisposition(disposition);
        request.ResponseHeaderOverrides.CacheControl = DownloadHeaders.CacheControl;
        return new DownloadLink(new Uri(clients.Signing.GetPreSignedURL(request)), expiresAt);
    }
}

/// <summary>
/// Readiness of the document store (DEC-326): the bucket (or Azure container) must be reachable with the configured credentials. A
/// failure is "Degraded", not "Unhealthy": everything but documents still works, so the API stays in rotation.
/// </summary>
internal sealed class DocumentStorageHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var provider = services.GetRequiredService<IOptions<DocumentStorageOptions>>().Value.Provider;
            if (provider == DocumentStorageProvider.AzureBlob)
            {
                var azure = services.GetRequiredService<AzureBlobClients>();
                await using var pages = azure.Container.GetBlobsAsync(cancellationToken: timeout.Token).AsPages(pageSizeHint: 1).GetAsyncEnumerator(timeout.Token);
                await pages.MoveNextAsync();
                return HealthCheckResult.Healthy();
            }

            var clients = services.GetRequiredService<S3Clients>();
            var response = await clients.Requests.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = clients.Options.Bucket, MaxKeys = 1 }, timeout.Token);
            return response.HttpStatusCode == HttpStatusCode.OK
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("Document storage answered unexpectedly.");
        }
#pragma warning disable CA1031 // Any failure (configuration, network, credentials) is reported, never thrown.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Degraded("Document storage is not available.", exception);
        }
    }
}

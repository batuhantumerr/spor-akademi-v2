using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SporAkademi.Application.Abstractions.Storage;

namespace SporAkademi.Infrastructure.Storage;

/// <summary>
/// Settings of the S3-compatible document store (section <c>Storage</c>, DEC-320). Read and validated on first use,
/// so the API starts without them; the <c>storage</c> readiness check then reports "Degraded" (DEC-326).
/// </summary>
public sealed class DocumentStorageOptions
{
    public const string Section = "Storage";

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
        request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition(disposition);
        request.ResponseHeaderOverrides.CacheControl = "private, no-store";
        return new DownloadLink(new Uri(clients.Signing.GetPreSignedURL(request)), expiresAt);
    }

    /// <summary>RFC 6266: an ASCII fallback name plus the UTF-8 name, so Turkish file names survive.</summary>
    internal static string ContentDisposition(DownloadDisposition disposition)
    {
        var ascii = new StringBuilder(disposition.FileName.Length);
        foreach (var c in disposition.FileName)
        {
            ascii.Append(c is >= ' ' and <= '~' and not '"' and not '\\' and not ';' and not '%' ? c : '_');
        }

        var kind = disposition.Inline ? "inline" : "attachment";
        return $"{kind}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(disposition.FileName)}";
    }
}

/// <summary>
/// Readiness of the document store (DEC-326): the bucket must be reachable with the configured credentials. A
/// failure is "Degraded", not "Unhealthy": everything but documents still works, so the API stays in rotation.
/// </summary>
internal sealed class DocumentStorageHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var clients = (S3Clients)services.GetService(typeof(S3Clients))!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
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

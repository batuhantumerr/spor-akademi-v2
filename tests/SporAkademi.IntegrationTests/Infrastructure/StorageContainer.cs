using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace SporAkademi.IntegrationTests.Infrastructure;

/// <summary>
/// The document store for tests: the SeaweedFS image of compose.yaml (Apache-2.0, DEC-320) started the same way
/// (<c>weed mini</c>, credentials from the environment, bucket created on start). Started once per run.
/// </summary>
public sealed class StorageContainer(string image) : IAsyncDisposable
{
    public const string Bucket = "spor-akademi-test-documents";
    private const string AccessKey = "storagetestaccess1";
    private const string SecretKey = "storageTestSecret1";
    private const int S3Port = 8333;

    private readonly IContainer _container = new ContainerBuilder(image)
        .WithCommand("mini", "-dir=/data", $"-bucket={Bucket}", "-master.telemetry=false", "-admin.ui=false", "-webdav=false", "-s3.port.iceberg=0", "-s3.port.lance=0")
        .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(S3Port).ForPath("/healthz")))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";

    /// <summary>The API's <c>Storage</c> settings for this container.</summary>
    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Storage:ServiceUrl"] = ServiceUrl,
        ["Storage:AccessKey"] = AccessKey,
        ["Storage:SecretKey"] = SecretKey,
        ["Storage:Bucket"] = Bucket,
    };

    public async ValueTask StartAsync()
    {
        await _container.StartAsync();

        // The bucket is created right after the S3 port opens; wait until a signed request sees it.
        using var client = CreateClient();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 });
                return;
            }
            catch (AmazonS3Exception) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }

    /// <summary>A client with the store's credentials, for assertions on what is (not) stored.</summary>
    public IAmazonS3 CreateClient() => new AmazonS3Client(
        new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    /// <summary>Keys stored under a prefix (e.g. a school's id).</summary>
    public async Task<IReadOnlyList<string>> KeysAsync(string prefix)
    {
        using var client = CreateClient();
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix });
        return [.. (response.S3Objects ?? []).Select(o => o.Key)];
    }

    public async Task<byte[]> ReadAsync(string key)
    {
        using var client = CreateClient();
        using var response = await client.GetObjectAsync(Bucket, key);
        using var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory);
        return memory.ToArray();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

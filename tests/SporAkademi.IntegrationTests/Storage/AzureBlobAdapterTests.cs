using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SporAkademi.Application.Abstractions.Storage;
using SporAkademi.Infrastructure;
using SporAkademi.Infrastructure.Storage;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Storage;

/// <summary>Azure Blob specifics (P5-06, DEC-650): the user delegation signer, configuration, provider choice and readiness.</summary>
public sealed class AzureBlobAdapterTests(AzuriteFixture azurite) : IClassFixture<AzuriteFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Story", "P5-06")]
    public void Links_are_signed_with_a_user_delegation_key_that_is_fetched_once_and_renewed_before_it_ends()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var service = new FakeDelegationService(time);
        var requested = service.Requested;
        var signer = new UserDelegationSasSigner(service, new StorageClock(time));

        string Sign()
        {
            var builder = new BlobSasBuilder(BlobSasPermissions.Read, time.GetUtcNow().AddMinutes(5)) { BlobContainerName = "documents", BlobName = "a/b", Resource = "b" };
            return signer.Sign(builder);
        }

        var first = Sign();
        Sign();
        requested.Count.ShouldBe(1);
        // A delegation SAS, read only, for one blob, expiring with the link; no account key parameter ("sig" is the HMAC of the delegated key).
        first.ShouldContain("skoid=");
        first.ShouldContain("sp=r");
        first.ShouldContain("sr=b");
        first.ShouldContain("sig=");
        requested[0].ExpiresOn.ShouldBe(time.GetUtcNow() + UserDelegationSasSigner.KeyLifetime);

        // Within the renewal window the key is replaced, so a link never outlives its key.
        time.Advance(UserDelegationSasSigner.KeyLifetime - UserDelegationSasSigner.RenewBefore + TimeSpan.FromMinutes(1));
        Sign();
        requested.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task The_adapter_works_with_the_blob_name_the_application_generates()
    {
        var storage = azurite.CreateStorage();
        var key = $"{Guid.CreateVersion7()}/students/{Guid.CreateVersion7()}.pdf";

        await storage.PutAsync(key, [1, 2, 3], "application/pdf", Ct);

        (await azurite.Service.GetBlobContainerClient(AzuriteFixture.ContainerName).GetBlobClient(key).ExistsAsync(Ct)).Value.ShouldBeTrue();
        var properties = (await azurite.Service.GetBlobContainerClient(AzuriteFixture.ContainerName).GetBlobClient(key).GetPropertiesAsync(cancellationToken: Ct)).Value;
        properties.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task Readiness_is_healthy_when_the_container_is_reachable_and_degraded_when_it_is_not()
    {
        var healthy = await CheckAsync(azurite.CreateClients());
        healthy.Status.ShouldBe(HealthStatus.Healthy);

        // The same parity as S3: any failure (a missing container here) is Degraded, never thrown and never Unhealthy.
        var missing = await CheckAsync(azurite.CreateClients(container: "no-such-container"));
        missing.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public async Task Readiness_is_degraded_when_the_settings_are_incomplete()
    {
        using var services = Services(new() { ["Storage:Provider"] = "AzureBlob" });

        var result = await new DocumentStorageHealthCheck(services).CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Exception!.Message.ShouldContain("Storage:AccountName");
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void S3_stays_the_default_provider()
    {
        using var services = Services(new()
        {
            ["Storage:ServiceUrl"] = "http://127.0.0.1:1",
            ["Storage:AccessKey"] = "x",
            ["Storage:SecretKey"] = "y",
            ["Storage:Bucket"] = "b",
        });

        services.GetRequiredService<IDocumentStorage>().ShouldBeOfType<S3DocumentStorage>();
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void The_provider_setting_selects_azure_blob_with_a_managed_identity_and_no_key()
    {
        using var services = Services(new()
        {
            ["Storage:Provider"] = "AzureBlob",
            ["Storage:AccountName"] = "stsyntheticdocs",
            ["Storage:Container"] = "documents",
            ["Storage:ManagedIdentityClientId"] = Guid.CreateVersion7().ToString(),
        });

        services.GetRequiredService<IDocumentStorage>().ShouldBeOfType<AzureBlobDocumentStorage>();
        var options = services.GetRequiredService<IOptions<DocumentStorageOptions>>().Value;
        options.AzureEndpoint.ToString().ShouldBe("https://stsyntheticdocs.blob.core.windows.net/");
    }

    [Theory]
    [InlineData("Storage:AccountName", "Storage:Provider=AzureBlob;Storage:Container=documents")]
    [InlineData("Storage:Container", "Storage:Provider=AzureBlob;Storage:AccountName=stsyntheticdocs")]
    [InlineData("Storage:BlobServiceUrl", "Storage:Provider=AzureBlob;Storage:Container=documents;Storage:BlobServiceUrl=not-a-url")]
    [InlineData("Storage:ServiceUrl", "Storage:AccessKey=x;Storage:SecretKey=y;Storage:Bucket=b")]
    [InlineData("Storage:DownloadLinkLifetime", "Storage:Provider=AzureBlob;Storage:AccountName=a;Storage:Container=c;Storage:DownloadLinkLifetime=01:00:00")]
    [Trait("Story", "P5-06")]
    public void An_incomplete_configuration_names_the_setting_when_the_store_is_first_used(string setting, string pairs)
    {
        var values = pairs.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => (string?)p[1]);
        using var services = Services(values);

        var thrown = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IDocumentStorage>());

        thrown.Message.ShouldContain(setting);
    }

    [Fact]
    [Trait("Story", "P5-06")]
    public void Nothing_starts_without_storage_settings()
    {
        // DEC-326: the host starts without a store; only first use (and readiness) report it.
        Should.NotThrow(() => Services(new()).Dispose());
    }

    /// <summary>A blob service that hands out synthetic delegation keys and remembers what was asked for.</summary>
    private sealed class FakeDelegationService(TimeProvider time) : BlobServiceClient
    {
        public List<BlobGetUserDelegationKeyOptions> Requested { get; } = [];

        public override string AccountName => "stsyntheticdocs";

        public override Response<UserDelegationKey> GetUserDelegationKey(BlobGetUserDelegationKeyOptions options, CancellationToken cancellationToken = default)
        {
            Requested.Add(options);
            var key = BlobsModelFactory.UserDelegationKey(
                Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString(), options.StartsOn ?? time.GetUtcNow(), options.ExpiresOn,
                "b", "2025-01-05", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            return Response.FromValue(key, null!);
        }
    }

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddDocumentStorage(configuration).BuildServiceProvider();
    }

    private static async Task<HealthCheckResult> CheckAsync(AzureBlobClients clients)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clients);
        services.AddSingleton<IOptions<DocumentStorageOptions>>(Options.Create(clients.Options));
        await using var provider = services.BuildServiceProvider();
        return await new DocumentStorageHealthCheck(provider).CheckHealthAsync(new HealthCheckContext(), Ct);
    }
}

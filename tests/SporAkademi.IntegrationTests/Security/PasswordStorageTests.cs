using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Identity;
using SporAkademi.Domain.Identity;
using SporAkademi.IntegrationTests.Identity;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Security;

/// <summary>NFR-SEC-03: passwords are stored as ASP.NET Core Identity V3 hashes and no response ever carries one.</summary>
public sealed class PasswordStorageTests(DatabaseFixture database) : IdentityTest(database)
{
    private const byte FormatV3 = 0x01;
    private const uint HmacSha512 = 2;

    [Fact]
    public async Task Stored_hashes_are_salted_pbkdf2_v3_with_unique_salts()
    {
        var school = await TestSchools.CreateAsync(Api, "sifre-okulu");
        var first = await SeedUserAsync("bir@example.com", UserRole.Coach, school);
        var second = await SeedUserAsync("iki@example.com", UserRole.Coach, school);

        var hashes = new[] { (await LoadUserAsync(first.Id)).PasswordHash, (await LoadUserAsync(second.Id)).PasswordHash };

        foreach (var hash in hashes)
        {
            hash.ShouldNotContain(OwnPassword);
            var bytes = Convert.FromBase64String(hash);
            bytes[0].ShouldBe(FormatV3);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)).ShouldBe(HmacSha512);
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)).ShouldBeGreaterThanOrEqualTo(100_000u);
            var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9));
            saltLength.ShouldBeGreaterThanOrEqualTo(16u);
            (bytes.Length - 13 - (int)saltLength).ShouldBeGreaterThanOrEqualTo(32);
        }

        // Same password, different salt: equal passwords never produce equal hashes.
        hashes[0].ShouldNotBe(hashes[1]);
        await InScopeAsync(sp =>
        {
            sp.GetRequiredService<IPasswordHasher>().Verify(hashes[0], OwnPassword).ShouldBe(PasswordVerification.Success);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task No_response_schema_exposes_a_password_or_hash()
    {
        var document = JsonNode.Parse(await Client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken))!;
        var schemas = document["components"]!["schemas"]!.AsObject();

        // Every schema reachable from a response, following $ref, items and properties.
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<JsonNode?>();
        foreach (var path in document["paths"]!.AsObject())
        {
            foreach (var operation in path.Value!.AsObject())
            {
                pending.Push(operation.Value?["responses"]);
            }
        }

        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["$ref"]?.GetValue<string>() is { } reference)
                    {
                        var name = reference.Split('/')[^1];
                        if (reachable.Add(name))
                        {
                            pending.Push(schemas[name]);
                        }
                    }

                    foreach (var child in obj)
                    {
                        pending.Push(child.Value);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        pending.Push(item);
                    }

                    break;
            }
        }

        reachable.ShouldNotBeEmpty();
        var offending = reachable
            // Flags such as mustChangePassword (boolean) carry no secret; any other field is suspect.
            .SelectMany(name => (schemas[name]?["properties"]?.AsObject() ?? [])
                .Where(p => p.Value?["type"]?.ToJsonString() is not "\"boolean\"")
                .Select(p => $"{name}.{p.Key}"))
            .Where(field => field.Contains("hash", StringComparison.OrdinalIgnoreCase)
                || (field.Contains("password", StringComparison.OrdinalIgnoreCase) && !field.EndsWith(".temporaryPassword", StringComparison.Ordinal)))
            .ToList();

        // The only password ever returned is the one-time temporary password of a new or reset account (US-05).
        offending.ShouldBeEmpty();
    }
}

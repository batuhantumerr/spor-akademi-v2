using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SporAkademi.Application.Students;
using SporAkademi.Domain.Tenancy;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

/// <summary>One school with a School Admin client, plus request builders with synthetic data only.</summary>
public abstract class StudentsTest(DatabaseFixture database) : IntegrationTest(database)
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected SchoolId School { get; private set; }

    protected HttpClient Admin { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        School = await TestSchools.CreateAsync(Api, "okul-a");
        Admin = Api.SchoolAdminClient(School);
    }

    public override async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected static object EmergencyContact(string name = "Ayşe Deneme", string phone = "0532 111 22 44", string relationship = "Teyze") =>
        new { name, phone, relationship };

    protected static object NewGuardian(string phone = "0532 111 22 33", string relationship = "Mother", string firstName = "Ayşe", string lastName = "Deneme") =>
        new { newGuardian = new { firstName, lastName, phone, email = "veli@example.com" }, relationship };

    protected static object ExistingGuardian(Guid id, string relationship = "Father") => new { guardianId = id, relationship };

    /// <summary>A minor (born 2015) with one new guardian unless <paramref name="guardians"/> says otherwise.</summary>
    protected static object Minor(string firstName = "Ali", string lastName = "Deneme", string birthDate = "2015-05-01", object[]? guardians = null, object? emergencyContact = null) =>
        new
        {
            firstName,
            lastName,
            birthDate,
            gender = "Male",
            emergencyContact = emergencyContact ?? EmergencyContact(),
            guardians = guardians ?? [NewGuardian()],
        };

    protected static object Adult(string firstName = "Zeynep", string lastName = "Örnek", string birthDate = "1990-03-10", string? phone = "0533 222 33 44") =>
        new
        {
            firstName,
            lastName,
            birthDate,
            gender = "Female",
            phone,
            email = "ogrenci@example.com",
            emergencyContact = EmergencyContact(),
        };

    protected async Task<StudentDto> RegisterAsync(object request, HttpClient? client = null)
    {
        var response = await (client ?? Admin).PostAsJsonAsync("/api/v1/students", request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudentDto>(Json, Ct))!;
    }

    protected Task<StudentDto?> GetAsync(Guid id) => Admin.GetFromJsonAsync<StudentDto>($"/api/v1/students/{id}", Json, Ct);

    protected static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    protected static async Task<JsonElement> ErrorsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");

    protected static Uri Url(string path) => new(path, UriKind.Relative);
}

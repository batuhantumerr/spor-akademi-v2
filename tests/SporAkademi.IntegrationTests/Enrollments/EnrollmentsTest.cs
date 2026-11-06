using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Groups;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Enrollments;

/// <summary>
/// The groups fixture (school, sport, facility, two coaches) plus students registered over HTTP with
/// synthetic data. The test clock's "today" in Istanbul is Thursday 2027-01-14.
/// </summary>
public abstract class EnrollmentsTest(DatabaseFixture database) : GroupsTest(database)
{
    protected const string Today = "2027-01-14";

    private int _phones;

    /// <summary>A minor registered with a new guardian; every call uses fresh phone numbers.</summary>
    protected async Task<Guid> RegisterStudentAsync(string firstName = "Ali", string lastName = "Deneme", string birthDate = "2015-05-01", HttpClient? client = null)
    {
        var n = Interlocked.Increment(ref _phones);
        var response = await (client ?? Admin).PostAsJsonAsync("/api/v1/students", new
        {
            firstName,
            lastName,
            birthDate,
            gender = "Male",
            phone = Phone(n, 1),
            email = "ogrenci@example.com",
            emergencyContact = new { name = "Acil Kişi", phone = Phone(n, 2), relationship = "Teyze" },
            guardians = new[]
            {
                new { newGuardian = new { firstName = "Veli", lastName = lastName, phone = Phone(n, 3), email = "veli@example.com" }, relationship = "Mother" },
            },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return IdOf(await BodyOf(response));
    }

    /// <summary>Synthetic Turkish mobile numbers, distinct per student and role.</summary>
    protected static string Phone(int student, int role) =>
        string.Create(CultureInfo.InvariantCulture, $"0532 {role}{student / 100 % 100:00} {student % 100:00} 00");

    protected Task<HttpResponseMessage> EnrollAsync(Guid studentId, Guid groupId, string? startDate = null, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync("/api/v1/enrollments", new { studentId, groupId, startDate }, Ct);

    protected async Task<JsonElement> EnrollOkAsync(Guid studentId, Guid groupId, string? startDate = null)
    {
        var response = await EnrollAsync(studentId, groupId, startDate);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await BodyOf(response);
    }

    protected Task<HttpResponseMessage> EndAsync(Guid enrollmentId, string? date, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/v1/enrollments/{enrollmentId}/end", new { date }, Ct);

    /// <summary>A group with the given capacity (and coach), optionally scheduled.</summary>
    protected async Task<JsonElement> GroupAsync(string name, int capacity = 20, Domain.Identity.User? coach = null, params object[] slots)
    {
        var response = await PostGroupAsync(NewGroup(name, coach?.Id.Value, capacity: capacity));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var group = await BodyOf(response);
        return slots.Length == 0 ? group : await ScheduleAsync(group, slots);
    }

    protected async Task<JsonElement> StudentEnrollmentsAsync(Guid studentId) =>
        await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{studentId}/enrollments", Ct);
}

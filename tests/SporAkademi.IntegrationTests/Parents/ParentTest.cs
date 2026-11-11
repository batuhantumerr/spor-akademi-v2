using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Billing;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>
/// The billing fixture plus families: students registered with a guardian that has an e-mail address, invitations, the
/// invitation e-mail and a parent signed in through the real login (P3-01 – P3-03). Synthetic data only.
/// </summary>
public abstract class ParentTest(DatabaseFixture database) : BillingTest(database)
{
    protected const string ParentPassword = "Veli-Sifresi-2027";

    private int _families;

    /// <summary>A minor with one new guardian (fresh phone, the given e-mail); returns the student and the guardian.</summary>
    protected async Task<(Guid Student, Guid Guardian)> RegisterChildAsync(
        string firstName,
        string? guardianEmail,
        string lastName = "Aile",
        HttpClient? client = null)
    {
        var n = 500 + Interlocked.Increment(ref _families);
        var response = await (client ?? Admin).PostAsJsonAsync("/api/v1/students", new
        {
            firstName,
            lastName,
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = Phone(n, 2), relationship = "Teyze" },
            guardians = new[]
            {
                new { newGuardian = new { firstName = "Veli", lastName, phone = Phone(n, 3), email = guardianEmail }, relationship = "Mother" },
            },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var student = await BodyOf(response);
        return (IdOf(student), student.GetProperty("guardians")[0].GetProperty("guardianId").GetGuid());
    }

    /// <summary>A sibling: a minor linked to an existing guardian.</summary>
    protected async Task<Guid> RegisterSiblingAsync(string firstName, Guid guardian, string lastName = "Aile", HttpClient? client = null)
    {
        var n = 500 + Interlocked.Increment(ref _families);
        var response = await (client ?? Admin).PostAsJsonAsync("/api/v1/students", new
        {
            firstName,
            lastName,
            birthDate = "2016-03-01",
            gender = "Female",
            emergencyContact = new { name = "Acil Kişi", phone = Phone(n, 2), relationship = "Teyze" },
            guardians = new[] { new { guardianId = guardian, relationship = "Mother" } },
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return IdOf(await BodyOf(response));
    }

    protected Task<HttpResponseMessage> InviteAsync(Guid guardian, HttpClient? client = null) =>
        (client ?? Admin).PostAsync(new Uri($"/api/v1/guardians/{guardian}/invitation", UriKind.Relative), null, Ct);

    protected Task<HttpResponseMessage> RevokeAsync(Guid guardian, HttpClient? client = null) =>
        (client ?? Admin).DeleteAsync(new Uri($"/api/v1/guardians/{guardian}/invitation", UriKind.Relative), Ct);

    protected async Task<JsonElement> InvitationAsync(Guid guardian) =>
        await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/guardians/{guardian}/invitation", Ct);

    protected async Task<string> StatusOfAsync(Guid guardian) => (await InvitationAsync(guardian)).GetProperty("status").GetString()!;

    /// <summary>Runs the outbox until it is empty (the invitation e-mail is sent after commit).</summary>
    protected async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>The token of the newest invitation e-mail sent to <paramref name="email"/>.</summary>
    protected string InvitationTokenFor(string email)
    {
        var mail = Api.Emails.To(email).Last(m => m.Template == "identity.parent-invitation");
        return CapturingEmailSender.TokenIn(mail);
    }

    protected Task<HttpResponseMessage> AcceptAsync(string token, string password = ParentPassword) =>
        Http.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { token, newPassword = password }, Ct);

    /// <summary>Invites the guardian, delivers the e-mail, accepts it and signs in: a client with the parent's real access token.</summary>
    protected async Task<(HttpClient Client, Session Session)> SignedInParentAsync(Guid guardian, string email)
    {
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        (await AcceptAsync(InvitationTokenFor(email))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var session = await LoginOkAsync(email, ParentPassword);
        session.Body.User.Role.ShouldBe("Parent");
        var client = Api.CreateClient();
        client.Authorize(session.AccessToken);
        return (client, session);
    }

    protected Task<User> ParentUserAsync(string email) =>
        InScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Set<User>().AsNoTracking()
            .Include(u => u.RefreshTokens).SingleAsync(u => u.Email == Domain.SharedKernel.Email.Create(email).Value));

    /// <summary>Reads one value as the migrator (no tenant filter), e.g. whether a guardian row still exists.</summary>
    protected async Task<object?> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Database.MigratorConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(Ct);
    }

    protected static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    protected static IEnumerable<Guid> IdsIn(JsonElement array, string property = "id") =>
        array.EnumerateArray().Select(e => e.GetProperty(property).GetGuid());
}

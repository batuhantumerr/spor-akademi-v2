using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Identity;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>
/// P3-02 (DEC-379): a parent account never outlives its reason. When the guardian's last active child leaves, or the
/// guardian is deleted (unlinked from its last student, or with an anonymized student), the account is closed in the
/// same transaction and its sessions end; a deleted guardian's account also loses its personal data.
/// </summary>
[Trait("Story", "P3-02")]
public sealed class ParentAccountClosingTests(DatabaseFixture database) : ParentTest(database)
{
    private const string Email = "kapanis.veli@example.com";

    [Fact]
    public async Task When_the_last_active_child_leaves_the_account_closes_and_its_sessions_end()
    {
        var (first, guardian) = await RegisterChildAsync("Ali", Email);
        var second = await RegisterSiblingAsync("Ayşe", guardian);
        var (parent, session) = await SignedInParentAsync(guardian, Email);

        await LeaveAsync(first);
        (await StatusOfAsync(guardian)).ShouldBe("Active");
        IdsIn(await GetOkAsync(parent, "/api/v1/parent/children")).ShouldBe([second]);

        await LeaveAsync(second);

        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
        (await ParentUserAsync(Email)).RefreshTokens.ShouldAllBe(t => t.RevokedAt != null);
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetOkAsync(parent, "/api/v1/parent/children")).GetArrayLength().ShouldBe(0);
        parent.Dispose();
    }

    [Fact]
    public async Task A_reactivated_child_does_not_reopen_the_account_by_itself()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        using var _ = (await SignedInParentAsync(guardian, Email)).Client;
        await LeaveAsync(student);

        (await Admin.PostAsync(new Uri($"/api/v1/students/{student}/reactivate", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
        PastInvitationCooldown();
        (await InviteAsync(guardian)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOfAsync(guardian)).ShouldBe("Invited");
    }

    [Fact]
    public async Task A_guardian_removed_from_its_only_student_is_deleted_and_its_account_erased()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        var (parent, session) = await SignedInParentAsync(guardian, Email);

        await ReplaceGuardianAsync(student, "0532 777 00 01");

        (await ScalarAsync("SELECT count(*) FROM students.guardians WHERE id = @id", guardian)).ShouldBe(0L);
        var user = await InScopeAsync(sp => sp.GetRequiredService<SporAkademi.Infrastructure.Persistence.AppDbContext>().Set<User>()
            .FindAsync([UserId.From(session.Body.User.Id)], Ct).AsTask());
        user!.Status.ShouldBe(UserStatus.Deactivated);
        user.FirstName.ShouldBe(User.ErasedParentFirstName);
        user.Phone.ShouldBeNull();
        user.Email.Value.ShouldNotBe(Email);
        (await RefreshAsync(session.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetOkAsync(parent, "/api/v1/parent/children")).GetArrayLength().ShouldBe(0);
        parent.Dispose();

        // The address is free again: the new guardian can be invited with it.
        var (_, other) = await RegisterChildAsync("Veli", Email, lastName: "Yeni");
        (await InviteAsync(other)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_guardian_removed_from_one_child_keeps_access_to_the_other()
    {
        var (first, guardian) = await RegisterChildAsync("Ali", Email);
        var second = await RegisterSiblingAsync("Ayşe", guardian);
        var (parent, _) = await SignedInParentAsync(guardian, Email);

        await ReplaceGuardianAsync(first, "0532 777 00 02");

        (await StatusOfAsync(guardian)).ShouldBe("Active");
        IdsIn(await GetOkAsync(parent, "/api/v1/parent/children")).ShouldBe([second]);
        parent.Dispose();
    }

    [Fact]
    public async Task A_guardian_removed_from_its_last_active_child_but_kept_by_a_departed_one_is_closed()
    {
        var (active, guardian) = await RegisterChildAsync("Ali", Email);
        var departed = await RegisterSiblingAsync("Ayşe", guardian);
        using var _ = (await SignedInParentAsync(guardian, Email)).Client;
        await LeaveAsync(departed);
        (await StatusOfAsync(guardian)).ShouldBe("Active");

        await ReplaceGuardianAsync(active, "0532 777 00 03");

        (await StatusOfAsync(guardian)).ShouldBe("Revoked");
    }

    [Fact]
    public async Task Anonymizing_the_last_child_deletes_the_guardian_and_erases_its_account()
    {
        var (student, guardian) = await RegisterChildAsync("Ali", Email);
        using var _ = (await SignedInParentAsync(guardian, Email)).Client;
        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = "2024-06-01" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOfAsync(guardian)).ShouldBe("Revoked");

        (await InScopeAsync(sp => sp.GetRequiredService<StudentAnonymizationJob>().RunAsync(Ct))).ShouldBe(1);

        (await ScalarAsync("SELECT count(*) FROM students.guardians WHERE id = @id", guardian)).ShouldBe(0L);
        (await LoginAsync(Email, ParentPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var erased = await ScalarAsync("SELECT count(*) FROM identity.users WHERE email = 'kapanis.veli@example.com' OR (role = 'Parent' AND first_name <> 'Silinmiş')", Guid.Empty);
        erased.ShouldBe(0L);
    }

    private async Task LeaveAsync(Guid student) =>
        (await Admin.PostAsJsonAsync($"/api/v1/students/{student}/leave", new { date = Today }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

    /// <summary>Replaces the student's guardians with one new guardian (the old one is unlinked).</summary>
    private async Task ReplaceGuardianAsync(Guid student, string phone)
    {
        var current = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var response = await Admin.PutAsJsonAsync($"/api/v1/students/{student}/guardians", new
        {
            guardians = new[] { new { newGuardian = new { firstName = "Yeni", lastName = "Veli", phone, email = (string?)null }, relationship = "Father" } },
            version = VersionOf(current),
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }
}

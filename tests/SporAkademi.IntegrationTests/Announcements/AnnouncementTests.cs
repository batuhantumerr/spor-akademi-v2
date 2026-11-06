using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SporAkademi.Domain.Announcements;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.Tenancy;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Groups;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Announcements;

[Trait("Story", "US-25")]
public sealed class AnnouncementTests(DatabaseFixture database) : GroupsTest(database)
{
    private Task<HttpResponseMessage> PublishAsync(Guid groupId, string title = "Cumartesi antrenmanı", string body = "Saat 10:00'a alındı.", HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync($"/api/v1/groups/{groupId}/announcements", new { title, body }, Ct);

    private static string?[] Titles(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString())];

    [Fact]
    public async Task Published_announcement_reaches_the_groups_coach_home_screen()
    {
        var group = await CreateGroupAsync("U10 Futbol", Ayse);

        var response = await PublishAsync(IdOf(group));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await BodyOf(response);
        created.GetProperty("title").GetString().ShouldBe("Cumartesi antrenmanı");
        created.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        created.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(Api.Clock.GetUtcNow());
        using var coach = CoachClient(Ayse);
        var mine = await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements", Ct);
        Titles(mine).ShouldBe(["Cumartesi antrenmanı"]);
        mine.GetProperty("items")[0].GetProperty("body").GetString().ShouldBe("Saat 10:00'a alındı.");
    }

    [Fact]
    public async Task Lists_are_newest_first_and_paged()
    {
        var group = await CreateGroupAsync("U10 Futbol", Ayse);
        foreach (var title in new[] { "Bir", "İki", "Üç" })
        {
            await PublishAsync(IdOf(group), title);
            Api.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        using var coach = CoachClient(Ayse);
        Titles(await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/announcements", Ct)).ShouldBe(["Üç", "İki", "Bir"]);
        var second = await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements?pageSize=2&page=2", Ct);
        Titles(second).ShouldBe(["Bir"]);
        second.GetProperty("totalCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Coach_sees_only_own_active_groups()
    {
        var own = await CreateGroupAsync("A Grubu", Ayse);
        var archived = await CreateGroupAsync("Eski Grup", Ayse);
        var others = await CreateGroupAsync("Mehmet'in Grubu", Mehmet);
        await PublishAsync(IdOf(own), "Benim");
        await PublishAsync(IdOf(archived), "Eski");
        await PublishAsync(IdOf(others), "Başkası");
        await ArchiveAsync(IdOf(archived));
        using var coach = CoachClient(Ayse);

        Titles(await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements", Ct)).ShouldBe(["Benim"]);
        (await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(own)}/announcements", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var id in new[] { IdOf(archived), IdOf(others), Guid.CreateVersion7() })
        {
            var response = await coach.GetAsync(new Uri($"/api/v1/groups/{id}/announcements", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("Group.NotFound");
        }

        Titles(await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(archived)}/announcements", Ct)).ShouldBe(["Eski"]);
    }

    [Fact]
    public async Task Archived_or_unknown_group_takes_no_announcement()
    {
        var group = await CreateGroupAsync();
        await ArchiveAsync(IdOf(group));

        var archived = await PublishAsync(IdOf(group));
        var unknown = await PublishAsync(Guid.CreateVersion7());

        archived.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(archived)).ShouldBe("Group.Archived");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(unknown)).ShouldBe("Group.NotFound");
    }

    [Theory]
    [InlineData("", "Metin", "title", "Field.Required")]
    [InlineData("Başlık", "", "body", "Field.Required")]
    [InlineData(null, "Metin", "title", "Field.Required")]
    public async Task Title_and_body_are_required(string? title, string body, string field, string code)
    {
        var group = await CreateGroupAsync();

        var response = await Admin.PostAsJsonAsync($"/api/v1/groups/{IdOf(group)}/announcements", new { title, body }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Title_and_body_lengths_are_limited()
    {
        var group = await CreateGroupAsync();

        var title = await PublishAsync(IdOf(group), new string('a', 151));
        var body = await PublishAsync(IdOf(group), "Başlık", new string('b', 4001));
        var limits = await PublishAsync(IdOf(group), new string('a', 150), new string('b', 4000));

        (await BodyOf(title)).GetProperty("errors").GetProperty("title")[0].GetString().ShouldBe("Field.TooLong");
        (await BodyOf(body)).GetProperty("errors").GetProperty("body")[0].GetString().ShouldBe("Field.TooLong");
        limits.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Body_is_plain_text_returned_verbatim_with_its_line_breaks()
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        const string body = "<script>alert('x')</script>\n\nSatır 2 &amp; <b>kalın değil</b>\r\n  girintili satır";

        var created = await BodyOf(await PublishAsync(IdOf(group), "Duyuru", "  \n" + body + "\n\t "));

        created.GetProperty("body").GetString().ShouldBe(body);
        using var coach = CoachClient(Ayse);
        var mine = await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements", Ct);
        mine.GetProperty("items")[0].GetProperty("body").GetString().ShouldBe(body);
    }

    [Fact]
    public async Task Title_spaces_are_collapsed()
    {
        var group = await CreateGroupAsync();

        var created = await BodyOf(await PublishAsync(IdOf(group), "  Cumartesi \t  antrenmanı\n ", "Metin"));

        created.GetProperty("title").GetString().ShouldBe("Cumartesi antrenmanı");
    }

    [Fact]
    public async Task Lengths_are_checked_after_the_same_whitespace_normalisation_as_the_domain()
    {
        var group = await CreateGroupAsync();
        var paddedTitle = "   " + string.Join("  ", Enumerable.Repeat("abcd", 30)) + "   ";
        var paddedBody = "\n\n  " + new string('b', 4000) + "  \n";

        var fits = await PublishAsync(IdOf(group), paddedTitle, paddedBody);
        var blankTitle = await PublishAsync(IdOf(group), " \t\n ", "Metin");
        var blankBody = await PublishAsync(IdOf(group), "Başlık", " \n\t ");
        var longBody = await PublishAsync(IdOf(group), "Başlık", "  " + new string('b', 4001) + "  ");

        fits.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await BodyOf(fits)).GetProperty("title").GetString()!.Length.ShouldBe(149);
        (await BodyOf(blankTitle)).GetProperty("errors").GetProperty("title")[0].GetString().ShouldBe("Field.Required");
        (await BodyOf(blankBody)).GetProperty("errors").GetProperty("body")[0].GetString().ShouldBe("Field.Required");
        (await BodyOf(longBody)).GetProperty("errors").GetProperty("body")[0].GetString().ShouldBe("Field.TooLong");
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    public async Task Invalid_paging_is_a_field_error(string query, string field)
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        using var coach = CoachClient(Ayse);

        var ofGroup = await Admin.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/announcements?{query}", UriKind.Relative), Ct);
        var mine = await coach.GetAsync(new Uri($"/api/v1/me/announcements?{query}", UriKind.Relative), Ct);

        foreach (var response in new[] { ofGroup, mine })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await BodyOf(response)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Coach_cannot_read_another_coachs_active_group()
    {
        var others = await CreateGroupAsync("Mehmet'in Grubu", Mehmet);
        await PublishAsync(IdOf(others), "Sadece Mehmet");
        using var coach = CoachClient(Ayse);

        var response = await coach.GetAsync(new Uri($"/api/v1/groups/{IdOf(others)}/announcements", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(response)).ShouldBe("Group.NotFound");
        (await coach.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Coaches_cannot_publish_and_admins_have_no_coach_feed()
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        using var coach = CoachClient(Ayse);

        (await PublishAsync(IdOf(group), client: coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Admin.GetAsync(new Uri("/api/v1/me/announcements", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Story", "US-02")]
    public async Task Another_school_cannot_publish_to_or_read_the_group()
    {
        var group = await CreateGroupAsync("A Grubu", Ayse);
        await PublishAsync(IdOf(group), "Gizli");
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        using var adminB = Api.SchoolAdminClient(schoolB);
        var coachB = await SeedCoachAsync(schoolB);
        using var coachClientB = CoachClient(coachB);

        (await PublishAsync(IdOf(group), client: adminB)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await adminB.GetAsync(new Uri($"/api/v1/groups/{IdOf(group)}/announcements", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await coachClientB.GetFromJsonAsync<JsonElement>("/api/v1/me/announcements", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
        Titles(await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{IdOf(group)}/announcements", Ct)).ShouldBe(["Gizli"]);
    }

    [Fact]
    [Trait("Story", "US-02")]
    public async Task Composite_foreign_key_rejects_a_group_of_another_school()
    {
        var group = GroupId.From(IdOf(await CreateGroupAsync()));
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");

        var violation = await InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(Announcement.Publish(schoolB, group, UserId.From(Guid.CreateVersion7()), "Sızma", "Metin", Api.Clock.GetUtcNow()).Value);
            return await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        });

        var postgres = violation.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe("23503");
        postgres.ConstraintName.ShouldBe("fk_announcements_group_tenant_id_group_id");
    }
}

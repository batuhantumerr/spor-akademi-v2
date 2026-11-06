using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SporAkademi.Application.Enrollments.Contracts;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Organization;
using SporAkademi.Domain.Students;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Students;

[Trait("Story", "US-16")]
public sealed class SearchStudentsTests(DatabaseFixture database) : StudentsTest(database)
{
    [Theory]
    [InlineData("sukru")]
    [InlineData("Şükrü")]
    [InlineData("ŞÜKRÜ")]
    [InlineData("SUKRU")]
    [InlineData("ükr")]
    [InlineData("yilmaz")]
    [InlineData("YILMAZ")]
    [InlineData("şükrü yılmaz")]
    public async Task Search_ignores_case_and_turkish_letters(string search)
    {
        await RegisterAsync(Minor("Şükrü", "Yılmaz"));
        await RegisterAsync(Minor("Ali", "Deneme"));

        var names = await NamesAsync($"/api/v1/students?search={Uri.EscapeDataString(search)}");

        names.ShouldBe(["Şükrü Yılmaz"]);
    }

    [Fact]
    public async Task Search_treats_like_wildcards_as_text()
    {
        await RegisterAsync(Minor("Ali", "Deneme"));

        (await NamesAsync("/api/v1/students?search=%25")).ShouldBeEmpty();
        (await NamesAsync("/api/v1/students?search=_")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_filter_selects_active_or_left_students()
    {
        await RegisterAsync(Minor("Ali"));
        var leaving = await RegisterAsync(Minor("Veli"));
        await Admin.PostAsJsonAsync($"/api/v1/students/{leaving.Id}/leave", new { date = "2027-01-10" }, Ct);

        (await NamesAsync("/api/v1/students?status=Active")).ShouldBe(["Ali Deneme"]);
        (await NamesAsync("/api/v1/students?status=left")).ShouldBe(["Veli Deneme"]);
        (await NamesAsync("/api/v1/students")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Group_and_sport_filters_restrict_to_the_enrolled_students()
    {
        var ali = await RegisterAsync(Minor("Ali"));
        await RegisterAsync(Minor("Veli"));
        var groupId = Guid.CreateVersion7();
        var sportId = Guid.CreateVersion7();
        var filter = new FixedGroupFilter(groupId, sportId, StudentId.From(ali.Id));
        await using var api = Api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IStudentGroupFilter>(_ => filter))));
        using var admin = api.CreateClient();
        admin.Authorize(TestTokens.Create(Guid.CreateVersion7(), "SchoolAdmin", School.Value, Api.Clock.GetUtcNow()));

        (await NamesAsync($"/api/v1/students?groupId={groupId}", admin)).ShouldBe(["Ali Deneme"]);
        (await NamesAsync($"/api/v1/students?sportId={sportId}&search=veli", admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Before_enrollments_exist_no_student_is_in_a_group_or_sport()
    {
        await RegisterAsync(Minor("Ali"));

        (await NamesAsync($"/api/v1/students?groupId={Guid.CreateVersion7()}")).ShouldBeEmpty();
        (await NamesAsync($"/api/v1/students?sportId={Guid.CreateVersion7()}")).ShouldBeEmpty();
        (await NamesAsync("/api/v1/students")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Results_are_paged_with_20_by_default_and_at_most_100()
    {
        for (var i = 0; i < 23; i++)
        {
            await RegisterAsync(Minor($"Öğrenci{i:00}", guardians: [NewGuardian(phone: $"0532 111 22 {i:00}")]));
        }

        var first = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students", Ct);
        first.GetProperty("items").GetArrayLength().ShouldBe(20);
        first.GetProperty("pageSize").GetInt32().ShouldBe(20);
        first.GetProperty("totalCount").GetInt32().ShouldBe(23);
        first.GetProperty("totalPages").GetInt32().ShouldBe(2);
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students?page=2", Ct)).GetProperty("items").GetArrayLength().ShouldBe(3);
        (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students?pageSize=100", Ct)).GetProperty("items").GetArrayLength().ShouldBe(23);

        var tooLarge = await Admin.GetAsync(Url("/api/v1/students?pageSize=101"), Ct);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsOf(tooLarge)).GetProperty("pageSize")[0].GetString().ShouldBe("Field.Invalid");
        (await ErrorsOf(await Admin.GetAsync(Url("/api/v1/students?page=0"), Ct))).GetProperty("page")[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task Results_are_sorted_by_last_name_by_default_or_by_creation()
    {
        await RegisterAsync(Minor("Can", "Yıldız"));
        Api.Clock.Advance(TimeSpan.FromMinutes(1));
        await RegisterAsync(Minor("Ali", "Arslan"));
        Api.Clock.Advance(TimeSpan.FromMinutes(1));
        await RegisterAsync(Minor("Ece", "Kaya"));

        (await NamesAsync("/api/v1/students")).ShouldBe(["Ali Arslan", "Ece Kaya", "Can Yıldız"]);
        (await NamesAsync("/api/v1/students?sort=-lastName")).ShouldBe(["Can Yıldız", "Ece Kaya", "Ali Arslan"]);
        (await NamesAsync("/api/v1/students?sort=createdAt")).ShouldBe(["Can Yıldız", "Ali Arslan", "Ece Kaya"]);
        (await NamesAsync("/api/v1/students?sort=-createdAt")).ShouldBe(["Ece Kaya", "Ali Arslan", "Can Yıldız"]);
    }

    [Theory]
    [InlineData("sort=firstName", "sort")]
    [InlineData("status=Archived", "status")]
    public async Task Unknown_sort_fields_and_statuses_are_rejected(string parameter, string field)
    {
        var response = await Admin.GetAsync(Url($"/api/v1/students?{parameter}"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).GetProperty(field)[0].GetString().ShouldBe("Field.Invalid");
    }

    [Fact]
    public async Task List_items_carry_no_contact_data()
    {
        await RegisterAsync(Adult());

        var item = (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/students", Ct)).GetProperty("items")[0];

        item.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "firstName", "lastName", "birthDate", "gender", "status", "leftAt"]);
    }

    [Fact]
    [Trait("Story", "US-02")]
    public async Task A_school_only_finds_its_own_students()
    {
        await RegisterAsync(Minor("Şükrü", "Yılmaz"));
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        using var otherAdmin = Api.SchoolAdminClient(other);
        await RegisterAsync(Minor("Şükrü", "Başka"), otherAdmin);

        (await NamesAsync("/api/v1/students?search=sukru")).ShouldBe(["Şükrü Yılmaz"]);
        (await NamesAsync("/api/v1/students?search=sukru", otherAdmin)).ShouldBe(["Şükrü Başka"]);
    }

    private async Task<List<string>> NamesAsync(string url, HttpClient? client = null)
    {
        var page = await (client ?? Admin).GetFromJsonAsync<JsonElement>(url, Ct);
        return [.. page.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("firstName").GetString()} {i.GetProperty("lastName").GetString()}")];
    }

    private sealed class FixedGroupFilter(Guid groupId, Guid sportId, StudentId enrolled) : IStudentGroupFilter
    {
        public Task<IReadOnlyCollection<StudentId>?> StudentsEnrolledInAsync(GroupId? group, SportId? sport, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<StudentId>?>(group == GroupId.From(groupId) || sport == SportId.From(sportId) ? [enrolled] : []);
    }
}

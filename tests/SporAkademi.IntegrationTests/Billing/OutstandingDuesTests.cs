using System.Net;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

[Trait("Story", "US-24")]
public sealed class OutstandingDuesTests(DatabaseFixture database) : BillingTest(database)
{
    private static List<(string? Student, string? Group, string? Period, string Remaining)> Rows(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(d => (
            d.GetProperty("studentFirstName").GetString(),
            d.GetProperty("groupName").GetString(),
            d.GetProperty("period").GetString(),
            AmountOf(d.GetProperty("remaining"))))];

    [Fact]
    public async Task Months_not_fully_paid_are_listed_with_what_remains()
    {
        var (_, _, ali) = await EnrolledAsync("U10 Futbol", fee: "1000.00", firstName: "Ali");
        var (_, _, veli) = await EnrolledAsync("U12 Futbol", fee: "800.00", firstName: "Veli");
        await RecordOkAsync(ali, "2027-01", "250.50");
        await RecordOkAsync(veli, "2027-01", "800");

        var page = await OutstandingAsync();

        page.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var due = page.GetProperty("items")[0];
        due.GetProperty("enrollmentId").GetGuid().ShouldBe(ali);
        due.GetProperty("studentLastName").GetString().ShouldBe("Deneme");
        AmountOf(due.GetProperty("fee")).ShouldBe("1000.00");
        AmountOf(due.GetProperty("paid")).ShouldBe("250.50");
        AmountOf(due.GetProperty("remaining")).ShouldBe("749.50");
        due.GetProperty("remaining").GetProperty("currency").GetString().ShouldBe("TRY");
    }

    [Fact]
    public async Task Every_month_with_one_active_day_owes_the_full_fee_up_to_the_current_month()
    {
        var (_, _, ended) = await EnrolledAsync("U10 Futbol", fee: "1000.00", startDate: "2027-01-14", firstName: "Ali");
        await EnrolledAsync("U12 Futbol", fee: "500.00", startDate: "2027-01-14", firstName: "Veli");
        var march = AdvanceTo(new DateTimeOffset(2027, 3, 10, 9, 0, 0, TimeSpan.Zero));
        (await EndAsync(ended, "2027-02-01", march)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var rows = Rows(await OutstandingAsync(client: march));

        // Ended on 1 February: January and February in full (no proration), nothing for March.
        rows.ShouldBe([
            ("Ali", "U10 Futbol", "2027-01", "1000.00"),
            ("Veli", "U12 Futbol", "2027-01", "500.00"),
            ("Ali", "U10 Futbol", "2027-02", "1000.00"),
            ("Veli", "U12 Futbol", "2027-02", "500.00"),
            ("Veli", "U12 Futbol", "2027-03", "500.00"),
        ]);
    }

    [Fact]
    public async Task The_list_is_filtered_by_group_and_period()
    {
        var (_, u10, _) = await EnrolledAsync("U10 Futbol", fee: "1000.00", firstName: "Ali");
        await EnrolledAsync("U12 Futbol", fee: "500.00", firstName: "Veli");
        var february = AdvanceTo(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero));

        Rows(await OutstandingAsync($"?groupId={u10}", february)).Select(r => (r.Group, r.Period))
            .ShouldBe([("U10 Futbol", "2027-01"), ("U10 Futbol", "2027-02")]);
        Rows(await OutstandingAsync("?period=2027-02", february)).Select(r => (r.Group, r.Period))
            .ShouldBe([("U10 Futbol", "2027-02"), ("U12 Futbol", "2027-02")]);
        Rows(await OutstandingAsync($"?groupId={u10}&period=2027-01", february)).ShouldHaveSingleItem().Period.ShouldBe("2027-01");
        (await OutstandingAsync("?period=2027-03", february)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_list_is_paged()
    {
        await EnrolledAsync("U10 Futbol", firstName: "Ali");
        await EnrolledAsync("U12 Futbol", firstName: "Veli");
        await EnrolledAsync("U14 Futbol", firstName: "Can");

        var page = await OutstandingAsync("?page=2&pageSize=2");

        page.GetProperty("totalCount").GetInt32().ShouldBe(3);
        page.GetProperty("totalPages").GetInt32().ShouldBe(2);
        Rows(page).ShouldHaveSingleItem().Group.ShouldBe("U14 Futbol");
    }

    [Fact]
    public async Task An_overpaid_month_is_not_listed_and_the_excess_is_not_carried_over()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: "1000.00");
        await RecordOkAsync(enrollment, "2027-01", "1500");
        var february = AdvanceTo(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero));

        Rows(await OutstandingAsync(client: february)).ShouldBe([("Ali", "U10 Futbol", "2027-02", "1000.00")]);
    }

    [Fact]
    public async Task Groups_without_a_fee_owe_nothing()
    {
        await EnrolledAsync(fee: null);

        (await OutstandingAsync()).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Theory]
    [InlineData("?period=2027-1")]
    [InlineData("?pageSize=101")]
    public async Task Invalid_filters_are_rejected(string query)
    {
        (await Admin.GetAsync(new Uri($"/api/v1/dues/outstanding{query}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

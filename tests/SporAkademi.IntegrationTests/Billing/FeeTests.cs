using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

[Trait("Story", "US-22")]
public sealed class FeeTests(DatabaseFixture database) : BillingTest(database)
{
    [Fact]
    public async Task A_fee_is_defined_from_a_month_onward_with_money_as_text()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var response = await DefineFeeAsync(group, "2027-01", "1250");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/groups/{group}/fees");
        var history = await BodyOf(response);
        var fee = history.EnumerateArray().ShouldHaveSingleItem();
        fee.GetProperty("effectiveFrom").GetString().ShouldBe("2027-01");
        fee.GetProperty("amount").GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.String);
        AmountOf(fee.GetProperty("amount")).ShouldBe("1250.00");
        fee.GetProperty("amount").GetProperty("currency").GetString().ShouldBe("TRY");

        await DefineFeeOkAsync(group, "2027-06", "1400.5");
        var fees = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{group}/fees", Ct);
        fees.EnumerateArray().Select(f => (f.GetProperty("effectiveFrom").GetString(), AmountOf(f.GetProperty("amount"))))
            .ShouldBe([("2027-01", "1250.00"), ("2027-06", "1400.50")]);
    }

    [Fact]
    public async Task A_past_period_cannot_get_a_fee()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var response = await DefineFeeAsync(group, "2026-12", "1000.00");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Fee.PeriodInPast");
    }

    [Fact]
    public async Task One_fee_per_period()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));
        await DefineFeeOkAsync(group, "2027-02", "1000.00");

        var response = await DefineFeeAsync(group, "2027-02", "900.00");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("Fee.AlreadyDefined");
    }

    [Fact]
    public async Task Concurrent_definitions_for_one_period_keep_one()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => DefineFeeAsync(group, "2027-03", $"10{i}.00")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
        (await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/groups/{group}/fees", Ct)).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_fee_change_mid_year_keeps_the_amount_of_past_months()
    {
        var (_, group, enrollment) = await EnrolledAsync(fee: "1000.00");
        var march = AdvanceTo(new DateTimeOffset(2027, 3, 2, 9, 0, 0, TimeSpan.Zero));

        (await DefineFeeAsync(group, "2027-01", "1500.00", march)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await DefineFeeOkAsync(group, "2027-03", "1250.00", march);

        var dues = (await OutstandingAsync($"?groupId={group}", march)).GetProperty("items").EnumerateArray().ToList();
        dues.Select(d => (d.GetProperty("period").GetString(), AmountOf(d.GetProperty("fee"))))
            .ShouldBe([("2027-01", "1000.00"), ("2027-02", "1000.00"), ("2027-03", "1250.00")]);
        dues.ShouldAllBe(d => d.GetProperty("enrollmentId").GetGuid() == enrollment);
    }

    [Theory]
    [InlineData("12.345", "amount", "Money.Invalid")]
    [InlineData("-10", "amount", "Money.Invalid")]
    [InlineData("1.000,00", "amount", "Money.Invalid")]
    public async Task Malformed_amounts_are_rejected(string amount, string field, string code)
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var response = await DefineFeeAsync(group, "2027-02", amount);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty(field)[0].GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Amounts_must_be_text_not_json_numbers()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var response = await Admin.PostAsJsonAsync($"/api/v1/groups/{group}/fees", new { effectiveFrom = "2027-02", amount = new { amount = 1250.0, currency = "TRY" } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_period_is_rejected()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));

        var response = await DefineFeeAsync(group, "2027-13", "100.00");

        (await BodyOf(response)).GetProperty("errors").GetProperty("effectiveFrom")[0].GetString().ShouldBe("YearMonth.Invalid");
    }

    [Fact]
    public async Task Archived_groups_take_no_fee()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));
        (await ArchiveAsync(group)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await DefineFeeAsync(group, "2027-02", "100.00");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("Group.Archived");
    }

    [Fact]
    public async Task Fees_of_another_school_are_not_found()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));
        await DefineFeeOkAsync(group, "2027-01", "100.00");
        using var other = Api.SchoolAdminClient(await TestSchools.CreateAsync(Api, "okul-b"));

        (await other.GetAsync(new Uri($"/api/v1/groups/{group}/fees", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DefineFeeAsync(group, "2027-02", "100.00", other)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Coaches_cannot_see_or_set_fees()
    {
        var group = IdOf(await GroupAsync("U10 Futbol", coach: Ayse));
        using var coach = CoachClient(Ayse);

        (await coach.GetAsync(new Uri($"/api/v1/groups/{group}/fees", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DefineFeeAsync(group, "2027-02", "100.00", coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

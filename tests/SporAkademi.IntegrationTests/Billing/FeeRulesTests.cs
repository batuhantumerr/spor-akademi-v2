using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P2-F8 fee rules (DEC-370 – DEC-374): versioned per school like group fees, a sibling discount for students sharing a
/// guardian and a single charge for the month of a group change. Every consumer of dues (list, dashboard, online
/// payment, reminders) sees the same amounts. The clock starts on 2027-01-14, so the current period is 2027-01.
/// </summary>
[Trait("Story", "P2-F8")]
public sealed class FeeRulesTests(DatabaseFixture database) : BillingTest(database)
{
    private const string RulesUrl = "/api/v1/settings/billing-rules";

    private int _phones = 500;

    private Task<HttpResponseMessage> DefineRulesAsync(object body, HttpClient? client = null) =>
        (client ?? Admin).PostAsJsonAsync(RulesUrl, body, Ct);

    private static object Rules(string effectiveFrom = "2027-01", int second = 10, int third = 15, bool singleCharge = true) =>
        new { effectiveFrom, secondChildPercent = second, thirdAndLaterChildPercent = third, singleChargeOnGroupChange = singleCharge };

    private async Task DefineRulesOkAsync(object body, HttpClient? client = null) =>
        (await DefineRulesAsync(body, client)).StatusCode.ShouldBe(HttpStatusCode.Created);

    private async Task<JsonElement> RulesListAsync(HttpClient? client = null)
    {
        var response = await (client ?? Admin).GetAsync(new Uri(RulesUrl, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    private string NextPhone() => string.Create(CultureInfo.InvariantCulture, $"0535 {Interlocked.Increment(ref _phones):000} 00 00");

    private static object NewGuardianLink(string phone, string firstName, string email) =>
        new { newGuardian = new { firstName, lastName = "Kardeş", phone, email }, relationship = "Mother" };

    private static async Task<JsonElement> ErrorsOf(HttpResponseMessage response) => (await BodyOf(response)).GetProperty("errors");

    private async Task<HttpClient> OtherSchoolAdminAsync() => Api.SchoolAdminClient(await TestSchools.CreateAsync(Api, "okul-b"));

    private static object ExistingGuardianLink(Guid guardianId) => new { guardianId, relationship = "Father" };

    /// <summary>A minor with the given guardian links; returns the student and their guardian ids.</summary>
    private async Task<(Guid Student, Guid[] Guardians)> RegisterWithGuardiansAsync(string firstName, params object[] guardians)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", new
        {
            firstName,
            lastName = "Kardeş",
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = NextPhone(), relationship = "Teyze" },
            guardians,
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await BodyOf(response);
        return (IdOf(body), [.. body.GetProperty("guardians").EnumerateArray().Select(g => g.GetProperty("guardianId").GetGuid())]);
    }

    /// <summary>
    /// Three siblings through two guardians (Ali–Ayşe, Veli–Ayşe and Mehmet, Can–Mehmet), each in their own group:
    /// Ali 800, Veli 1000, Can 600.
    /// </summary>
    private async Task<(Guid Ali, Guid Veli, Guid Can)> SiblingsAsync()
    {
        var (aliStudent, aliGuardians) = await RegisterWithGuardiansAsync("Ali", NewGuardianLink(NextPhone(), "Ayşe", "ayse@example.com"));
        var (veliStudent, veliGuardians) = await RegisterWithGuardiansAsync("Veli", ExistingGuardianLink(aliGuardians[0]), NewGuardianLink(NextPhone(), "Mehmet", "mehmet@example.com"));
        var mehmet = veliGuardians.Single(g => g != aliGuardians[0]);
        var (canStudent, _) = await RegisterWithGuardiansAsync("Can", ExistingGuardianLink(mehmet));

        return (
            await EnrollInNewGroupAsync(aliStudent, "U10 Futbol", "800.00"),
            await EnrollInNewGroupAsync(veliStudent, "U12 Futbol", "1000.00"),
            await EnrollInNewGroupAsync(canStudent, "U14 Futbol", "600.00"));
    }

    private async Task<Guid> EnrollInNewGroupAsync(Guid student, string groupName, string fee, string startDate = "2027-01-04")
    {
        var group = IdOf(await GroupAsync(groupName));
        await DefineFeeOkAsync(group, "2027-01", fee);
        return IdOf(await EnrollOkAsync(student, group, startDate));
    }

    private static Dictionary<Guid, JsonElement> ByEnrollment(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().ToDictionary(d => d.GetProperty("enrollmentId").GetGuid());

    private static decimal Amount(JsonElement money) => decimal.Parse(AmountOf(money), CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_school_without_versions_lists_none_and_a_new_version_answers_201_with_every_version_newest_first()
    {
        (await RulesListAsync()).GetArrayLength().ShouldBe(0);

        var created = await DefineRulesAsync(Rules("2027-01", 10, 15, true));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.OriginalString.ShouldBe(RulesUrl);
        await DefineRulesOkAsync(Rules("2027-04", 5, 20, false));

        var versions = (await RulesListAsync()).EnumerateArray().ToList();
        versions.Select(v => v.GetProperty("effectiveFrom").GetString()).ShouldBe(["2027-04", "2027-01"]);
        var april = versions[0];
        april.GetProperty("secondChildPercent").GetInt32().ShouldBe(5);
        april.GetProperty("thirdAndLaterChildPercent").GetInt32().ShouldBe(20);
        april.GetProperty("singleChargeOnGroupChange").GetBoolean().ShouldBeFalse();
        april.GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_past_period_and_a_period_already_defined_are_refused_like_fees()
    {
        var past = await DefineRulesAsync(Rules("2026-12"));
        past.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(past)).ShouldBe("BillingRules.PeriodInPast");

        await DefineRulesOkAsync(Rules("2027-01"));
        var again = await DefineRulesAsync(Rules("2027-01", 20, 20));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(again)).ShouldBe("BillingRules.AlreadyDefined");
        (await RulesListAsync()).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_input_is_refused_field_by_field()
    {
        var empty = await DefineRulesAsync(new { });
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = await ErrorsOf(empty);
        foreach (var field in new[] { "effectiveFrom", "secondChildPercent", "thirdAndLaterChildPercent", "singleChargeOnGroupChange" })
        {
            errors.GetProperty(field)[0].GetString().ShouldBe("Field.Required");
        }

        var invalid = await DefineRulesAsync(new { effectiveFrom = "2027-13", secondChildPercent = 101, thirdAndLaterChildPercent = -1, singleChargeOnGroupChange = true });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        errors = await ErrorsOf(invalid);
        errors.GetProperty("effectiveFrom")[0].GetString().ShouldBe("YearMonth.Invalid");
        errors.GetProperty("secondChildPercent")[0].GetString().ShouldBe("BillingRules.InvalidPercent");
        errors.GetProperty("thirdAndLaterChildPercent")[0].GetString().ShouldBe("BillingRules.InvalidPercent");
        (await RulesListAsync()).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Only_the_school_admin_reaches_the_rules_and_each_school_sees_its_own()
    {
        using var coach = CoachClient(Ayse);
        (await coach.GetAsync(new Uri(RulesUrl, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DefineRulesAsync(Rules(), coach)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await DefineRulesOkAsync(Rules("2027-02"));
        using var other = await OtherSchoolAdminAsync();

        (await RulesListAsync(other)).GetArrayLength().ShouldBe(0);
        // The other school's own version for the same period does not collide with this school's.
        await DefineRulesOkAsync(Rules("2027-02", 50, 50), other);
        (await RulesListAsync()).EnumerateArray().ShouldHaveSingleItem().GetProperty("secondChildPercent").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Without_rules_siblings_owe_the_full_fee()
    {
        var (ali, veli, can) = await SiblingsAsync();

        var dues = ByEnrollment(await OutstandingAsync());

        Amount(dues[ali].GetProperty("fee")).ShouldBe(800m);
        Amount(dues[veli].GetProperty("fee")).ShouldBe(1000m);
        Amount(dues[can].GetProperty("fee")).ShouldBe(600m);
        dues.Values.ShouldAllBe(d => Amount(d.GetProperty("discount")) == 0m && d.GetProperty("adjustment").GetString() == "None");
    }

    [Fact]
    public async Task Siblings_linked_through_two_guardians_are_discounted_and_the_highest_fee_pays_in_full()
    {
        var (ali, veli, can) = await SiblingsAsync();
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(ali, "2027-01", "100.00");

        var dues = ByEnrollment(await OutstandingAsync());

        // Veli (1000) pays in full, Ali (800) is the second child, Can (600) the third: linked to Ali only through Veli.
        Amount(dues[veli].GetProperty("fee")).ShouldBe(1000m);
        dues[veli].GetProperty("adjustment").GetString().ShouldBe("None");
        var aliDue = dues[ali];
        (Amount(aliDue.GetProperty("listFee")), Amount(aliDue.GetProperty("discount")), Amount(aliDue.GetProperty("fee")), Amount(aliDue.GetProperty("remaining")))
            .ShouldBe((800m, 80m, 720m, 620m));
        aliDue.GetProperty("adjustment").GetString().ShouldBe("SiblingDiscount");
        aliDue.GetProperty("discountPercent").GetInt32().ShouldBe(10);
        (Amount(dues[can].GetProperty("fee")), dues[can].GetProperty("discountPercent").GetInt32()).ShouldBe((510m, 15));

        // The group filter does not change the ranking: siblings in other groups still count.
        var u14Only = ByEnrollment(await OutstandingAsync($"?period=2027-01&groupId={await GroupOfAsync(can)}"));
        Amount(u14Only.ShouldHaveSingleItem().Value.GetProperty("fee")).ShouldBe(510m);
    }

    [Fact]
    public async Task Past_months_keep_their_bill_when_a_version_starts_later()
    {
        var (ali, _, _) = await SiblingsAsync();
        await DefineRulesOkAsync(Rules("2027-02", 10, 15));
        var february = AdvanceTo(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero));

        var rows = (await OutstandingAsync("?pageSize=100", february)).GetProperty("items").EnumerateArray()
            .Where(d => d.GetProperty("enrollmentId").GetGuid() == ali)
            .Select(d => (d.GetProperty("period").GetString(), Amount(d.GetProperty("fee"))))
            .ToList();

        rows.ShouldBe([("2027-01", 800m), ("2027-02", 720m)]);
    }

    [Fact]
    public async Task The_month_of_a_group_change_is_charged_once()
    {
        await DefineRulesOkAsync(Rules("2027-01", 0, 0, singleCharge: true));
        var (student, _, first) = await EnrolledAsync("U10 Futbol", fee: "1000.00", startDate: "2027-01-04");
        (await EndAsync(first, "2027-01-10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await EnrollInNewGroupAsync(student, "U12 Futbol", "1200.00", startDate: "2027-01-10");
        var february = AdvanceTo(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero));

        var rows = (await OutstandingAsync("?pageSize=100", february)).GetProperty("items").EnumerateArray()
            .Select(d => (d.GetProperty("enrollmentId").GetGuid(), d.GetProperty("period").GetString(), Amount(d.GetProperty("fee"))))
            .ToList();

        rows.ShouldBe([(first, "2027-01", 1000m), (second, "2027-02", 1200m)], ignoreOrder: true);

        // Nothing is due for the new group in January, so no online payment can start for it.
        var start = await StartOnlineAsync(second, "2027-01", $"key-{Guid.NewGuid():N}", february);
        start.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(start)).ShouldBe("OnlinePayment.NothingDue");
    }

    [Fact]
    public async Task Without_the_group_change_rule_both_enrollments_are_charged_for_the_month()
    {
        await DefineRulesOkAsync(Rules("2027-01", 10, 15, singleCharge: false));
        var (student, _, first) = await EnrolledAsync("U10 Futbol", fee: "1000.00", startDate: "2027-01-04");
        (await EndAsync(first, "2027-01-10")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await EnrollInNewGroupAsync(student, "U12 Futbol", "1200.00", startDate: "2027-01-10");

        var dues = ByEnrollment(await OutstandingAsync("?period=2027-01"));

        (Amount(dues[first].GetProperty("fee")), Amount(dues[second].GetProperty("fee"))).ShouldBe((1000m, 1200m));
    }

    [Fact]
    public async Task The_dashboard_agrees_with_the_discounted_dues()
    {
        var (ali, _, _) = await SiblingsAsync();
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(ali, "2027-01", "20.00");

        var response = await Admin.GetAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dashboard = await BodyOf(response);
        var dues = await OutstandingAsync("?pageSize=100");

        var remaining = dues.GetProperty("items").EnumerateArray().Sum(d => Amount(d.GetProperty("remaining")));
        remaining.ShouldBe(1000m + 720m + 510m - 20m);
        Amount(dashboard.GetProperty("kpis").GetProperty("outstandingTotal")).ShouldBe(remaining);
        Amount(dashboard.GetProperty("kpis").GetProperty("outstandingThisMonth")).ShouldBe(remaining);
        var january = dashboard.GetProperty("monthlyBilling").EnumerateArray().Single(m => m.GetProperty("period").GetString() == "2027-01");
        Amount(january.GetProperty("due")).ShouldBe(1000m + 720m + 510m);
    }

    [Fact]
    public async Task An_online_payment_is_for_the_discounted_remaining_and_a_payment_above_it_is_flagged_as_overpaid()
    {
        var (ali, _, can) = await SiblingsAsync();
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(ali, "2027-01", "200.00");

        var started = await StartOnlineOkAsync(ali);
        AmountOf(started.GetProperty("amount")).ShouldBe("520.00");

        // A cash payment of Can's whole list fee is accepted; the due (510.00) is settled and leaves the list.
        await RecordOkAsync(can, "2027-01", "600.00");
        ByEnrollment(await OutstandingAsync()).ShouldNotContainKey(can);
        var noneDue = await StartOnlineAsync(can, "2027-01", $"key-{Guid.NewGuid():N}");
        (await CodeOf(noneDue)).ShouldBe("OnlinePayment.NothingDue");
    }

    [Fact]
    public async Task Reminders_state_the_discounted_amount()
    {
        var (_, veli, can) = await SiblingsAsync();
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(veli, "2027-01", "1000.00");
        await RecordOkAsync(can, "2027-01", "510.00");

        (await RunReminderJobAsync()).ShouldBe(1);
        await DeliverAsync();

        // Ali's guardian (Ayşe) is reminded of 720.00, not the list fee.
        var mail = Api.Emails.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe("ayse@example.com");
        mail.TextBody.ShouldContain("₺720,00");
        mail.TextBody.ShouldNotContain("₺800,00");
    }

    private async Task<Guid> GroupOfAsync(Guid enrollment) =>
        ByEnrollment(await OutstandingAsync("?pageSize=100"))[enrollment].GetProperty("groupId").GetGuid();

    private async Task<int> RunReminderJobAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DuesReminderJob>().RunAsync(Ct);
    }

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }
}

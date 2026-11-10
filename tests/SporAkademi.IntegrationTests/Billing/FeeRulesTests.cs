using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;
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
        var family = await FamilyAsync();
        return (family.Ali, family.Veli, family.Can);
    }

    /// <summary>The <see cref="SiblingsAsync"/> family with the student and guardian ids, for changing links later.</summary>
    private async Task<Family> FamilyAsync()
    {
        var (aliStudent, aliGuardians) = await RegisterWithGuardiansAsync("Ali", NewGuardianLink(NextPhone(), "Ayşe", "ayse@example.com"));
        var (veliStudent, veliGuardians) = await RegisterWithGuardiansAsync("Veli", ExistingGuardianLink(aliGuardians[0]), NewGuardianLink(NextPhone(), "Mehmet", "mehmet@example.com"));
        var mehmet = veliGuardians.Single(g => g != aliGuardians[0]);
        var (canStudent, _) = await RegisterWithGuardiansAsync("Can", ExistingGuardianLink(mehmet));

        return new Family(
            await EnrollInNewGroupAsync(aliStudent, "U10 Futbol", "800.00"),
            await EnrollInNewGroupAsync(veliStudent, "U12 Futbol", "1000.00"),
            await EnrollInNewGroupAsync(canStudent, "U14 Futbol", "600.00"),
            veliStudent,
            mehmet);
    }

    private sealed record Family(Guid Ali, Guid Veli, Guid Can, Guid VeliStudent, Guid Mehmet);

    /// <summary>Replaces a student's guardians (US-13) with the given existing ones.</summary>
    private static async Task ReplaceGuardiansAsync(Guid student, HttpClient client, params Guid[] guardians)
    {
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/v1/students/{student}", Ct);
        var response = await client.PutAsJsonAsync(
            $"/api/v1/students/{student}/guardians",
            new { guardians = guardians.Select(ExistingGuardianLink).ToArray(), version = current.GetProperty("version").GetUInt32() },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<int> RunCloseJobAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BillingPeriodCloseJob>().RunAsync(Ct);
    }

    private static async Task<JsonElement> DashboardAsync(HttpClient client)
    {
        var response = await client.GetAsync(new Uri("/api/v1/dashboard", UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    private static decimal DueOf(JsonElement dashboard, string period) =>
        Amount(dashboard.GetProperty("monthlyBilling").EnumerateArray().Single(m => m.GetProperty("period").GetString() == period).GetProperty("due"));

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

    [Fact]
    public async Task A_closed_month_paid_with_a_sibling_discount_stays_paid_when_the_family_link_is_removed()
    {
        var family = await FamilyAsync();
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(family.Ali, "2027-01", "720.00");
        await RecordOkAsync(family.Veli, "2027-01", "1000.00");
        await RecordOkAsync(family.Can, "2027-01", "510.00");

        var february = AdvanceTo(new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero));
        (await RunCloseJobAsync()).ShouldBe(1);
        (await RunCloseJobAsync()).ShouldBe(0); // idempotent

        // Veli leaves Ayşe: Ali has no sibling any more (Can keeps Veli through Mehmet).
        await ReplaceGuardiansAsync(family.VeliStudent, february, family.Mehmet);

        // January is closed: Ali's 720.00 still settles it; no due, no reminder. February (open) follows today's links.
        var dues = (await OutstandingAsync("?pageSize=100", february)).GetProperty("items").EnumerateArray()
            .Select(d => (d.GetProperty("enrollmentId").GetGuid(), d.GetProperty("period").GetString(), Amount(d.GetProperty("fee"))))
            .ToList();
        dues.ShouldNotContain(d => d.Item2 == "2027-01");
        dues.ShouldContain((family.Ali, "2027-02", 800m));
        DueOf(await DashboardAsync(february), "2027-01").ShouldBe(720m + 1000m + 510m);

        AdvanceTo(new DateTimeOffset(2027, 2, 11, 6, 0, 0, TimeSpan.Zero));
        await RunReminderJobAsync();
        await DeliverAsync();
        Api.Emails.Sent.ShouldNotContain(m => m.Subject.StartsWith("Ocak 2027", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Linking_siblings_later_does_not_discount_a_closed_month()
    {
        var (aliStudent, aliGuardians) = await RegisterWithGuardiansAsync("Ali", NewGuardianLink(NextPhone(), "Ayşe", "ayse@example.com"));
        var (zeynepStudent, _) = await RegisterWithGuardiansAsync("Zeynep", NewGuardianLink(NextPhone(), "Fatma", "fatma@example.com"));
        await EnrollInNewGroupAsync(aliStudent, "U10 Futbol", "1000.00");
        var zeynep = await EnrollInNewGroupAsync(zeynepStudent, "U12 Futbol", "800.00");
        await DefineRulesOkAsync(Rules("2027-01", 10, 15));
        await RecordOkAsync(zeynep, "2027-01", "800.00");

        var february = AdvanceTo(new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero));
        (await RunCloseJobAsync()).ShouldBe(1);
        await ReplaceGuardiansAsync(zeynepStudent, february, aliGuardians[0]);

        // January stays at the full fees it closed with (no hidden overpayment); February is discounted.
        var dashboard = await DashboardAsync(february);
        DueOf(dashboard, "2027-01").ShouldBe(1800m);
        DueOf(dashboard, "2027-02").ShouldBe(1000m + 720m);
        ByEnrollment(await OutstandingAsync("?period=2027-01", february)).ShouldNotContainKey(zeynep);
    }

    [Fact]
    public async Task A_planned_version_can_be_withdrawn_but_a_started_one_cannot()
    {
        await DefineRulesOkAsync(Rules("2027-01"));
        await DefineRulesOkAsync(Rules("2027-03", 20, 20));

        var started = await Admin.DeleteAsync(new Uri($"{RulesUrl}/2027-01", UriKind.Relative), Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(started)).ShouldBe("BillingRules.AlreadyStarted");

        (await Admin.DeleteAsync(new Uri($"{RulesUrl}/2027-03", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RulesListAsync()).EnumerateArray().Select(v => v.GetProperty("effectiveFrom").GetString()).ShouldBe(["2027-01"]);

        foreach (var missing in new[] { "2027-03", "2027-13", "abc" })
        {
            var response = await Admin.DeleteAsync(new Uri($"{RulesUrl}/{missing}", UriKind.Relative), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("BillingRules.NotFound");
        }

        using var coach = CoachClient(Ayse);
        (await coach.DeleteAsync(new Uri($"{RulesUrl}/2027-01", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Readiness_is_degraded_while_a_past_month_under_rules_stays_open_after_the_grace_days()
    {
        await DefineRulesOkAsync(Rules("2027-01"));
        var health = Api.Services.GetRequiredService<HealthCheckService>();
        async Task<HealthStatus> StatusAsync() => (await health.CheckHealthAsync(c => c.Name == "billing-close", Ct)).Entries["billing-close"].Status;

        (await StatusAsync()).ShouldBe(HealthStatus.Healthy); // January is the current month
        AdvanceTo(new DateTimeOffset(2027, 2, 2, 23, 0, 0, TimeSpan.Zero));
        (await StatusAsync()).ShouldBe(HealthStatus.Healthy); // grace days
        AdvanceTo(new DateTimeOffset(2027, 2, 3, 9, 0, 0, TimeSpan.Zero));
        (await StatusAsync()).ShouldBe(HealthStatus.Degraded); // the close job has not closed January

        (await RunCloseJobAsync()).ShouldBe(1);
        (await StatusAsync()).ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_single_family_is_found_through_indirect_links_and_cycles_within_the_school_only()
    {
        // Ali–g1–Ece–g2–Can, and Can also linked to g1 (a cycle); Zeynep is unrelated.
        var (ali, aliGuardians) = await RegisterWithGuardiansAsync("Ali", NewGuardianLink(NextPhone(), "Ayşe", "ayse@example.com"));
        var g1 = aliGuardians[0];
        var (ece, eceGuardians) = await RegisterWithGuardiansAsync("Ece", ExistingGuardianLink(g1), NewGuardianLink(NextPhone(), "Mehmet", "mehmet@example.com"));
        var g2 = eceGuardians.Single(g => g != g1);
        var (can, _) = await RegisterWithGuardiansAsync("Can", ExistingGuardianLink(g2), new { guardianId = g1, relationship = "Mother" });
        var (zeynep, _) = await RegisterWithGuardiansAsync("Zeynep", NewGuardianLink(NextPhone(), "Fatma", "fatma@example.com"));

        // Another school's family with the same shape must never show up.
        var otherSchool = await TestSchools.CreateAsync(Api, "okul-b");
        using var other = Api.SchoolAdminClient(otherSchool);
        var foreign = await other.PostAsJsonAsync("/api/v1/students", new
        {
            firstName = "Yabancı",
            lastName = "Kardeş",
            birthDate = "2015-05-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = NextPhone(), relationship = "Teyze" },
            guardians = new[] { NewGuardianLink(NextPhone(), "Hülya", "hulya@example.com") },
        }, Ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Created);
        var foreignStudent = IdOf(await BodyOf(foreign));

        var expected = new[] { ali, ece, can }.Select(StudentId.From).OrderBy(s => s.Value).ToList();
        foreach (var member in new[] { ali, ece, can })
        {
            (await FamilyOfAsync(School, member)).ShouldBe(expected);
        }

        (await FamilyOfAsync(School, zeynep)).ShouldBe([StudentId.From(zeynep)]);
        // Asked in school A, B's student has no links there; asked in B, it never reaches A's family.
        (await FamilyOfAsync(School, foreignStudent)).ShouldBe([StudentId.From(foreignStudent)]);
        (await FamilyOfAsync(otherSchool, foreignStudent)).ShouldBe([StudentId.From(foreignStudent)]);
        (await FamilyOfAsync(otherSchool, ali)).ShouldBe([StudentId.From(ali)]);
    }

    private async Task<IReadOnlyList<StudentId>> FamilyOfAsync(SchoolId school, Guid student)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SporAkademi.Infrastructure.Persistence.BackgroundTenantContext>().Enter(school.Value);
        return await scope.ServiceProvider.GetRequiredService<IStudentSiblings>().GetFamilyAsync(StudentId.From(student), Ct);
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

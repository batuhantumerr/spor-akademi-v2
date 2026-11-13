using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P4-11 (DEC-592): a payment refunded in a later month, also after its month was closed (DEC-420, DEC-421). The clock
/// moves forward through the API's <c>TimeProvider</c>. The rule: the refunded payment leaves every total; the due is
/// owed again; collection is cash basis by payment date, so the refund takes the money out of the month it was paid in
/// and the refund's own month gets no negative amount. The dashboard and the reports are read once before the refund,
/// so their cached copies (DEC-311) must also be replaced.
/// </summary>
[Trait("Story", "P4-11")]
[Trait("Roadmap", "P3-13")]
public sealed class CrossMonthRefundTests(DatabaseFixture database) : OnlinePaymentTest(database)
{
    private const string JanuaryToFebruary = "/api/v1/reports/revenue?from=2027-01&to=2027-02";
    private const string JanuaryToMarch = "/api/v1/reports/revenue?from=2027-01&to=2027-03";

    /// <summary>
    /// One group (1000 a month from January 2027), two students enrolled from 2027-01-04: Ali pays January online on
    /// 2027-01-14, Veli pays January in cash on 2027-01-10. Returns Ali's enrollment and online payment and Veli's
    /// enrollment.
    /// </summary>
    private async Task<(Guid Ali, Guid AliPayment, Guid Veli)> JanuaryPaidAsync()
    {
        var (_, group, ali) = await EnrolledAsync();
        var veli = IdOf(await EnrollOkAsync(await RegisterStudentAsync("Veli"), group, "2027-01-04"));
        var (_, payment) = await PayOnlineAsync(ali);
        await RecordOkAsync(veli, "2027-01", "1000.00");
        return (ali, payment, veli);
    }

    [Fact]
    public async Task A_refund_in_the_next_month_takes_the_money_out_of_the_month_it_was_paid_in()
    {
        var (ali, payment, veli) = await JanuaryPaidAsync();
        using var february = AdvanceTo(new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero));
        await RecordOkAsync(veli, "2027-02", "1000.00", february, paidOn: "2027-02-05");

        var report = await GetOkAsync(february, JanuaryToFebruary);
        Periods(report).ShouldBe(
        [
            "2027-01 accrued 2000.00 forPeriod 2000.00 outstanding 0.00 inMonth 2000.00",
            "2027-02 accrued 2000.00 forPeriod 1000.00 outstanding 1000.00 inMonth 1000.00",
        ]);
        var dashboard = await GetOkAsync(february, "/api/v1/dashboard");
        Kpis(dashboard).ShouldBe("thisMonth 1000.00 outstandingThisMonth 1000.00 outstandingTotal 1000.00");
        Months(dashboard).ShouldContain("2027-01 collected 2000.00 due 2000.00");
        Months(dashboard).ShouldContain("2027-02 collected 1000.00 due 2000.00");

        await RefundOkAsync(payment, "Ocak antrenmanları yapılmadı", february);

        // January (the month the money came in) loses it; February, the refund's month, keeps its 1000 and goes no lower.
        Periods(await GetOkAsync(february, JanuaryToFebruary)).ShouldBe(
        [
            "2027-01 accrued 2000.00 forPeriod 1000.00 outstanding 1000.00 inMonth 1000.00",
            "2027-02 accrued 2000.00 forPeriod 1000.00 outstanding 1000.00 inMonth 1000.00",
        ]);
        dashboard = await GetOkAsync(february, "/api/v1/dashboard");
        Kpis(dashboard).ShouldBe("thisMonth 1000.00 outstandingThisMonth 1000.00 outstandingTotal 2000.00");
        Months(dashboard).ShouldContain("2027-01 collected 1000.00 due 2000.00");
        Months(dashboard).ShouldContain("2027-02 collected 1000.00 due 2000.00");

        // Ali owes January again (the enrollment is active) and can pay it again online.
        var january = (await OutstandingAsync("?period=2027-01", february)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        january.GetProperty("enrollmentId").GetGuid().ShouldBe(ali);
        AmountOf(january.GetProperty("remaining")).ShouldBe("1000.00");
        (await StartOnlineAsync(ali, "2027-01", "ocak-yeniden", february)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_refund_after_the_month_closed_keeps_the_frozen_accrual_and_reopens_only_the_paid_amount()
    {
        // Months are closed only under fee rules (DEC-375); the two students are not siblings, so no discount applies.
        (await Admin.PostAsJsonAsync("/api/v1/settings/billing-rules", new
        {
            effectiveFrom = "2027-01",
            secondChildPercent = 10,
            thirdAndLaterChildPercent = 15,
            singleChargeOnGroupChange = true,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var (ali, payment, veli) = await JanuaryPaidAsync();
        // January closes on 2027-02-02; the refund comes in March, two months after the payment.
        using (AdvanceTo(new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero)))
        {
            (await InScopeAsync(services => services.GetRequiredService<BillingPeriodCloseJob>().RunAsync(Ct))).ShouldBe(1);
        }

        using var march = AdvanceTo(new DateTimeOffset(2027, 3, 5, 9, 0, 0, TimeSpan.Zero));
        await RecordOkAsync(veli, "2027-03", "1000.00", march, paidOn: "2027-03-02");
        var reportBefore = await GetOkAsync(march, JanuaryToMarch);
        Periods(reportBefore)[0].ShouldBe("2027-01 accrued 2000.00 forPeriod 2000.00 outstanding 0.00 inMonth 2000.00");
        var dashboardBefore = await GetOkAsync(march, "/api/v1/dashboard");
        var snapshot = "SELECT count(*) FROM billing.charge_snapshots WHERE period = '2027-01' AND fee_amount = 1000";
        (await CountAsync(snapshot)).ShouldBe(2);

        await RefundOkAsync(payment, "Yanlış öğrenciye ödendi", march);

        // DEC-421: January's charges stay as they closed; only the refunded 1000 is owed again, by Ali.
        (await CountAsync(snapshot)).ShouldBe(2);
        var reportAfter = await GetOkAsync(march, JanuaryToMarch);
        Periods(reportAfter).ShouldBe(
        [
            "2027-01 accrued 2000.00 forPeriod 1000.00 outstanding 1000.00 inMonth 1000.00",
            Periods(reportBefore)[1],
            Periods(reportBefore)[2],
        ]);
        Amount(reportAfter.GetProperty("total").GetProperty("collectedInMonth"))
            .ShouldBe(Amount(reportBefore.GetProperty("total").GetProperty("collectedInMonth")) - 1000m);
        var january = (await OutstandingAsync("?period=2027-01", march)).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        january.GetProperty("enrollmentId").GetGuid().ShouldBe(ali);
        AmountOf(january.GetProperty("fee")).ShouldBe("1000.00");
        AmountOf(january.GetProperty("remaining")).ShouldBe("1000.00");

        // The dashboard: March's collection is untouched, the outstanding total grows by exactly the refund.
        var dashboardAfter = await GetOkAsync(march, "/api/v1/dashboard");
        Kpi(dashboardAfter, "collectedThisMonth").ShouldBe(Kpi(dashboardBefore, "collectedThisMonth"));
        Kpi(dashboardAfter, "collectedThisMonth").ShouldBe(1000m);
        Kpi(dashboardAfter, "outstandingThisMonth").ShouldBe(Kpi(dashboardBefore, "outstandingThisMonth"));
        Kpi(dashboardAfter, "outstandingTotal").ShouldBe(Kpi(dashboardBefore, "outstandingTotal") + 1000m);
        Months(dashboardBefore).ShouldContain("2027-01 collected 2000.00 due 2000.00");
        Months(dashboardAfter).ShouldContain("2027-01 collected 1000.00 due 2000.00");
        Months(dashboardAfter).ShouldContain("2027-03 collected 1000.00 due 2000.00");
    }

    private static async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyOf(response);
    }

    private static decimal Amount(JsonElement money) => decimal.Parse(AmountOf(money), CultureInfo.InvariantCulture);

    private static decimal Kpi(JsonElement dashboard, string name) => Amount(dashboard.GetProperty("kpis").GetProperty(name));

    private static string Kpis(JsonElement dashboard)
    {
        var kpis = dashboard.GetProperty("kpis");
        return $"thisMonth {AmountOf(kpis.GetProperty("collectedThisMonth"))} " +
            $"outstandingThisMonth {AmountOf(kpis.GetProperty("outstandingThisMonth"))} " +
            $"outstandingTotal {AmountOf(kpis.GetProperty("outstandingTotal"))}";
    }

    private static string[] Months(JsonElement dashboard) =>
    [
        .. dashboard.GetProperty("monthlyBilling").EnumerateArray().Select(m =>
            $"{m.GetProperty("period").GetString()} collected {AmountOf(m.GetProperty("collected"))} due {AmountOf(m.GetProperty("due"))}"),
    ];

    private static string[] Periods(JsonElement report) =>
    [
        .. report.GetProperty("periods").EnumerateArray()
            .Select(p => $"{p.GetProperty("period").GetString()} accrued {AmountOf(p.GetProperty("accrued"))} " +
                $"forPeriod {AmountOf(p.GetProperty("collectedForPeriod"))} outstanding {AmountOf(p.GetProperty("outstanding"))} " +
                $"inMonth {AmountOf(p.GetProperty("collectedInMonth"))}"),
    ];
}

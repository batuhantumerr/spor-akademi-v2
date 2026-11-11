using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>
/// P3-03 (DEC-385, DEC-386): what a signed-in parent reads about their children and how they pay a due online. The
/// test clock's today is Thursday 2027-01-14 (15:00 in Istanbul), so a 10:00 session of today was held.
/// </summary>
[Trait("Story", "P3-03")]
public sealed class ParentApiTests(DatabaseFixture database) : ParentTest(database)
{
    private const string Email = "aile.veli@example.com";
    private const string SessionKey = "2027-01-14T10:00";

    private Guid _group;
    private Guid _first;
    private Guid _second;
    private Guid _firstEnrollment;
    private Guid _secondEnrollment;
    private HttpClient _parent = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _group = IdOf(await GroupAsync("U10 Futbol", 20, Ayse, Slot("Thursday", "10:00", "11:00"), Slot("Monday", "17:00", "18:00")));
        await DefineFeeOkAsync(_group, "2027-01", "1000.00");
        // P2-F8: 10 % off for the second child, so the two siblings owe different amounts.
        (await Admin.PostAsJsonAsync("/api/v1/settings/billing-rules", new { effectiveFrom = "2027-01", secondChildPercent = 10, thirdAndLaterChildPercent = 15, singleChargeOnGroupChange = true }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (_first, var guardian) = await RegisterChildAsync("Ali", Email);
        _second = await RegisterSiblingAsync("Ayşe", guardian);
        _firstEnrollment = IdOf(await EnrollOkAsync(_first, _group, "2027-01-04"));
        _secondEnrollment = IdOf(await EnrollOkAsync(_second, _group, "2027-01-04"));
        _parent = (await SignedInParentAsync(guardian, Email)).Client;
    }

    public override async ValueTask DisposeAsync()
    {
        _parent.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task The_parent_sees_both_children_without_emergency_contacts_or_guardian_data()
    {
        var children = await GetOkAsync(_parent, "/api/v1/parent/children");

        IdsIn(children).ShouldBe([_first, _second], ignoreOrder: true);
        children[0].GetProperty("relationship").GetString().ShouldBe("Mother");
        var raw = children.GetRawText();
        raw.ShouldNotContain("emergency", Case.Insensitive);
        raw.ShouldNotContain("Acil Kişi");
        raw.ShouldNotContain("phone", Case.Insensitive);
    }

    [Fact]
    public async Task The_weekly_program_names_the_group_place_coach_and_slots_of_active_enrollments()
    {
        var other = IdOf(await GroupAsync("U12 Futbol", 20, Mehmet, Slot("Tuesday", "18:00", "19:00")));
        var ended = IdOf(await EnrollOkAsync(_first, other, "2027-01-04"));
        (await EndAsync(ended, "2027-01-10")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var schedule = await GetOkAsync(_parent, $"/api/v1/parent/children/{_first}/schedule");

        var group = schedule.EnumerateArray().ShouldHaveSingleItem();
        group.GetProperty("groupId").GetGuid().ShouldBe(_group);
        group.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        group.GetProperty("sportName").GetString().ShouldBe("Futbol");
        group.GetProperty("facilityName").GetString().ShouldBe("A Sahası");
        group.GetProperty("coachFirstName").GetString().ShouldBe(Ayse.FirstName);
        group.GetProperty("startDate").GetString().ShouldBe("2027-01-04");
        var slots = group.GetProperty("slots");
        slots.GetArrayLength().ShouldBe(2);
        slots[0].GetProperty("day").GetString().ShouldBe("Monday");
        slots[1].GetProperty("day").GetString().ShouldBe("Thursday");
        slots[1].GetProperty("start").GetString().ShouldBe("10:00");
        slots[1].GetProperty("end").GetString().ShouldBe("11:00");
        group.GetRawText().ShouldNotContain(Ayse.Email.Value);
    }

    [Fact]
    public async Task Attendance_history_shows_what_the_coach_recorded()
    {
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{_group}/sessions/{SessionKey}/attendance", new
        {
            records = new[] { new { studentId = _first, status = "Present" }, new { studentId = _second, status = "Absent" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var history = await GetOkAsync(_parent, $"/api/v1/parent/children/{_first}/attendance");

        history.GetProperty("from").GetString().ShouldBe("2026-12-16");
        history.GetProperty("to").GetString().ShouldBe(Today);
        var today = history.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("sessionKey").GetString() == SessionKey);
        today.GetProperty("status").GetString().ShouldBe("Present");
        history.GetProperty("summary").GetProperty("present").GetInt32().ShouldBe(1);
        // The sibling's record is not in this child's history.
        history.GetRawText().ShouldNotContain(_second.ToString());

        var ranged = await GetOkAsync(_parent, $"/api/v1/parent/children/{_second}/attendance?from=2027-01-01&to=2027-01-31");
        ranged.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("sessionKey").GetString() == SessionKey)
            .GetProperty("status").GetString().ShouldBe("Absent");
    }

    [Fact]
    public async Task Dues_are_exactly_what_the_school_admin_sees_including_the_sibling_discount()
    {
        await RecordOkAsync(_firstEnrollment, "2027-01", "100.00");
        var admin = (await OutstandingAsync()).GetProperty("items");

        foreach (var child in new[] { _first, _second })
        {
            var mine = await GetOkAsync(_parent, $"/api/v1/parent/children/{child}/dues");
            var expected = admin.EnumerateArray().Where(r => r.GetProperty("studentId").GetGuid() == child).Select(r => r.GetRawText()).ToList();

            expected.ShouldNotBeEmpty();
            mine.EnumerateArray().Select(r => r.GetRawText()).ToList().ShouldBe(expected);
        }

        var discounted = admin.EnumerateArray().Where(r => r.GetProperty("discountPercent").GetInt32() == 10).ShouldHaveSingleItem();
        AmountOf(discounted.GetProperty("fee")).ShouldBe("900.00");
    }

    [Fact]
    public async Task Payments_list_only_recorded_payments_without_cancel_details()
    {
        await RecordOkAsync(_firstEnrollment, "2027-01", "300.00");
        var wrong = IdOf(await RecordOkAsync(_firstEnrollment, "2027-01", "50.00"));
        (await CancelAsync(wrong, "Yanlış tutar girildi")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await GetOkAsync(_parent, $"/api/v1/parent/children/{_first}/payments");

        var payment = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        AmountOf(payment.GetProperty("amount")).ShouldBe("300.00");
        payment.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        page.GetProperty("totalCount").GetInt32().ShouldBe(1);
        page.GetRawText().ShouldNotContain("Yanlış");
        page.GetRawText().ShouldNotContain("cancel", Case.Insensitive);
        (await GetOkAsync(_parent, $"/api/v1/parent/children/{_second}/payments")).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Announcements_are_those_of_the_childs_current_groups_without_the_author()
    {
        var other = IdOf(await GroupAsync("U14 Futbol"));
        (await Admin.PostAsJsonAsync($"/api/v1/groups/{_group}/announcements", new { title = "Antrenman saati", body = "Perşembe antrenmanı 10:30'da." }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Admin.PostAsJsonAsync($"/api/v1/groups/{other}/announcements", new { title = "Başka grup", body = "Bu duyuru bu aileye değil." }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var page = await GetOkAsync(_parent, $"/api/v1/parent/children/{_first}/announcements");

        var item = page.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("title").GetString().ShouldBe("Antrenman saati");
        item.GetProperty("groupName").GetString().ShouldBe("U10 Futbol");
        page.GetRawText().ShouldNotContain("Başka grup");
        page.GetRawText().ShouldNotContain("createdBy", Case.Insensitive);
    }

    [Fact]
    public async Task The_parent_pays_a_due_online_through_the_same_flow_once_per_due()
    {
        var started = await StartAsync(_firstEnrollment, "2027-01", "veli-odeme-0001");

        started.StatusCode.ShouldBe(HttpStatusCode.Created);
        var attempt = await BodyOf(started);
        attempt.GetProperty("studentId").GetGuid().ShouldBe(_first);
        attempt.GetProperty("checkoutUrl").GetString().ShouldNotBeNullOrEmpty();
        var admin = (await OutstandingAsync()).GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("enrollmentId").GetGuid() == _firstEnrollment);
        AmountOf(attempt.GetProperty("amount")).ShouldBe(AmountOf(admin.GetProperty("remaining")));

        // The same key and body is a replay of the same attempt.
        var replayed = await StartAsync(_firstEnrollment, "2027-01", "veli-odeme-0001");
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayed.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        IdOf(await BodyOf(replayed)).ShouldBe(IdOf(attempt));

        // Another key while the first checkout is open: one open attempt per due.
        var second = await StartAsync(_firstEnrollment, "2027-01", "veli-odeme-0002");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(second)).ShouldBe("OnlinePayment.CheckoutInProgress");

        var read = await GetOkAsync(_parent, $"/api/v1/parent/online-payments/{IdOf(attempt)}");
        read.GetProperty("status").GetString().ShouldBe(attempt.GetProperty("status").GetString());
        read.GetRawText().ShouldNotContain("idempotency", Case.Insensitive);
    }

    [Fact]
    public async Task Starting_an_online_payment_needs_an_idempotency_key()
    {
        var response = await StartAsync(_secondEnrollment, "2027-01", key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyOf(response)).GetProperty("errors").GetProperty("idempotencyKey")[0].GetString().ShouldBe("Idempotency.KeyRequired");
    }

    [Fact]
    public async Task The_parent_cannot_reach_school_admin_or_coach_endpoints()
    {
        foreach (var url in new[] { "/api/v1/students", $"/api/v1/students/{_first}", "/api/v1/dues/outstanding", "/api/v1/me/groups", $"/api/v1/groups/{_group}/roster" })
        {
            (await _parent.GetAsync(new Uri(url, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, url);
        }

        var me = await GetOkAsync(_parent, "/api/v1/me");
        me.GetProperty("role").GetString().ShouldBe("Parent");
    }

    private async Task<HttpResponseMessage> StartAsync(Guid enrollmentId, string period, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/parent/online-payments", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enrollmentId, period }),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await _parent.SendAsync(request, Ct);
    }
}

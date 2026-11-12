using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Application.Abstractions.Data;
using SporAkademi.Application.Abstractions.Outbox;
using SporAkademi.Application.Announcements.Contracts;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Realtime;

/// <summary>
/// P3-08 (DEC-396 – DEC-400): who may connect, who receives what, and when. Two families train in group X (coach Ayşe),
/// a third family in group Y (coach Mehmet); another school has its own admin. Every notice is checked at its
/// recipients and at everyone who must not get it: the other family, the other group's coach and parent, the other
/// school. Notices go out only when the outbox delivers a committed change.
/// </summary>
[Trait("Story", "P3-08")]
public sealed class RealtimeNotificationTests(DatabaseFixture database) : RealtimeTest(database)
{
    private const string SessionKey = "2027-01-14T10:00";

    private Guid _groupX;
    private Guid _groupY;
    private (Guid Student, Guid Enrollment, string Token) _a;
    private (Guid Student, Guid Enrollment, string Token) _b;
    private (Guid Student, Guid Enrollment, string Token) _c;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _groupX = IdOf(await GroupAsync("U10 Futbol", 20, Ayse, Slot("Thursday", "10:00", "11:00")));
        _groupY = IdOf(await GroupAsync("U12 Futbol", 20, Mehmet, Slot("Thursday", "10:00", "11:00")));
        await DefineFeeOkAsync(_groupX, "2027-01", "1000.00");
        _a = await FamilyAsync(_groupX, "Arda", "arda.veli@example.com");
        _b = await FamilyAsync(_groupX, "Berk", "berk.veli@example.com");
        _c = await FamilyAsync(_groupY, "Cem", "cem.veli@example.com");
        // The families' own setup (invitation e-mails) is delivered; nothing realtime is pending from it.
        await DeliverAsync();
    }

    [Fact]
    public async Task A_hub_connection_needs_a_valid_school_token_and_the_query_token_only_counts_on_the_hub()
    {
        using var anonymous = Api.CreateClient();
        (await NegotiateAsync(anonymous, null)).ShouldBe(HttpStatusCode.Unauthorized);
        (await NegotiateAsync(anonymous, "kesinlikle-gecersiz")).ShouldBe(HttpStatusCode.Unauthorized);
        (await NegotiateAsync(anonymous, Token(Guid.CreateVersion7(), "PlatformAdmin", tenant: null))).ShouldBe(HttpStatusCode.Forbidden);
        (await NegotiateAsync(anonymous, TestTokens.Create(Guid.CreateVersion7(), "Parent", School.Value, Api.Clock.GetUtcNow(), passwordChangeRequired: true)))
            .ShouldBe(HttpStatusCode.Forbidden);

        // What browsers do for WebSockets: the token in the query.
        (await NegotiateAsync(anonymous, _a.Token)).ShouldBe(HttpStatusCode.OK);
        foreach (var role in new[] { "SchoolAdmin", "Coach" })
        {
            (await NegotiateAsync(anonymous, Token(Guid.CreateVersion7(), role, School.Value))).ShouldBe(HttpStatusCode.OK);
        }

        // Outside the hub a query token is ignored.
        (await anonymous.GetAsync(new Uri($"/api/v1/parent/children?access_token={_a.Token}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // An expired token is refused too.
        var expired = Token(Guid.CreateVersion7(), "Coach", School.Value, Api.Clock.GetUtcNow().AddHours(-1));
        (await NegotiateAsync(anonymous, expired)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Attendance_reaches_only_that_childs_parents()
    {
        var parentA = await ListenAsync(_a.Token);
        var parentB = await ListenAsync(_b.Token);
        var parentC = await ListenAsync(_c.Token);
        var coach = await ListenAsync(Token(Ayse.Id.Value, "Coach", School.Value));
        var admin = await ListenAsync(Token(Guid.CreateVersion7(), "SchoolAdmin", School.Value));

        (await Admin.PutAsJsonAsync($"/api/v1/groups/{_groupX}/sessions/{SessionKey}/attendance", new
        {
            records = new[] { new { studentId = _a.Student, status = "Present" }, new { studentId = _b.Student, status = "Absent" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();

        var forA = await parentA.WaitForAsync(OfType("attendance.recorded"), Arrives);
        Text(forA, "studentId").ShouldBe(_a.Student.ToString());
        Text(forA, "status").ShouldBe("Present");
        Text(forA, "date").ShouldBe("2027-01-14");
        Text(forA, "groupId").ShouldBe(_groupX.ToString());
        var forB = await parentB.WaitForAsync(OfType("attendance.recorded"), Arrives);
        Text(forB, "studentId").ShouldBe(_b.Student.ToString());
        Text(forB, "status").ShouldBe("Absent");

        await Task.Delay(StaysQuiet, Ct);
        parentA.Received.ShouldHaveSingleItem();
        parentB.Received.ShouldHaveSingleItem();
        parentC.Received.ShouldBeEmpty();
        coach.Received.ShouldBeEmpty();
        admin.Received.ShouldBeEmpty();
        // Ids, date and status only: no names in any notice.
        forA.GetRawText().ShouldNotContain("Arda");

        // Saving the same statuses again changes nothing and tells nobody.
        (await Admin.PutAsJsonAsync($"/api/v1/groups/{_groupX}/sessions/{SessionKey}/attendance", new
        {
            records = new[] { new { studentId = _a.Student, status = "Present" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        await Task.Delay(StaysQuiet, Ct);
        parentA.Received.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_announcement_reaches_the_groups_coach_its_parents_and_the_schools_admins_only()
    {
        var parentA = await ListenAsync(_a.Token);
        var parentB = await ListenAsync(_b.Token);
        var parentC = await ListenAsync(_c.Token);
        var coachX = await ListenAsync(Token(Ayse.Id.Value, "Coach", School.Value));
        var coachY = await ListenAsync(Token(Mehmet.Id.Value, "Coach", School.Value));
        var admin = await ListenAsync(Token(Guid.CreateVersion7(), "SchoolAdmin", School.Value));
        var schoolB = await TestSchools.CreateAsync(Api, "okul-b");
        var adminB = await ListenAsync(Token(Guid.CreateVersion7(), "SchoolAdmin", schoolB.Value));
        // A coach id of school A in a school B token: the groups are per school, so it receives nothing.
        var coachXInSchoolB = await ListenAsync(Token(Ayse.Id.Value, "Coach", schoolB.Value));

        var published = await Admin.PostAsJsonAsync($"/api/v1/groups/{_groupX}/announcements", new { title = "Yarın antrenman yok", body = "Saha bakımda." }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.Created);
        var announcement = IdOf(await BodyOf(published)).ToString();
        await DeliverAsync();

        foreach (var recipient in new[] { parentA, parentB, coachX, admin })
        {
            var notice = await recipient.WaitForAsync(OfType("announcement.published"), Arrives);
            Text(notice, "id").ShouldBe(announcement);
            Text(notice, "groupId").ShouldBe(_groupX.ToString());
            notice.GetRawText().ShouldNotContain("antrenman yok");
        }

        await Task.Delay(StaysQuiet, Ct);
        parentA.Received.ShouldHaveSingleItem();
        coachX.Received.ShouldHaveSingleItem();
        parentC.Received.ShouldBeEmpty();
        coachY.Received.ShouldBeEmpty();
        adminB.Received.ShouldBeEmpty();
        coachXInSchoolB.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payment_reaches_only_that_childs_parents()
    {
        var parentA = await ListenAsync(_a.Token);
        var parentB = await ListenAsync(_b.Token);
        var coach = await ListenAsync(Token(Ayse.Id.Value, "Coach", School.Value));

        var payment = IdOf(await RecordOkAsync(_a.Enrollment, "2027-01", "400.00")).ToString();
        await DeliverAsync();

        var notice = await parentA.WaitForAsync(OfType("payment.recorded"), Arrives);
        Text(notice, "id").ShouldBe(payment);
        Text(notice, "studentId").ShouldBe(_a.Student.ToString());
        notice.GetRawText().ShouldNotContain("400");
        await Task.Delay(StaysQuiet, Ct);
        parentB.Received.ShouldBeEmpty();
        coach.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Notices_go_out_only_after_commit_and_never_for_a_rolled_back_change()
    {
        var coach = await ListenAsync(Token(Ayse.Id.Value, "Coach", School.Value));

        // Committed, but the outbox has not delivered yet: nothing is pushed from inside the request.
        (await Admin.PostAsJsonAsync($"/api/v1/groups/{_groupX}/announcements", new { title = "Duyuru", body = "Metin." }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await Task.Delay(StaysQuiet, Ct);
        coach.Received.ShouldBeEmpty();
        await DeliverAsync();
        await coach.WaitForAsync(OfType("announcement.published"), Arrives);

        // A change whose transaction rolls back leaves no message behind, so nobody is told.
        var rolledBack = Guid.CreateVersion7();
        await InScopeAsync(async services =>
        {
            var result = await services.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
                _ =>
                {
                    services.GetRequiredService<IOutbox>().Enqueue(new AnnouncementPublishedMessage(rolledBack, _groupX), School.Value);
                    return Task.FromResult(Result.Failure<bool>(Error.Conflict("Test.RolledBack", "Rolled back on purpose.")));
                },
                Ct);
            result.IsFailure.ShouldBeTrue();
        });
        (await Api.Services.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(Ct)).ShouldBe(0);
        await Task.Delay(StaysQuiet, Ct);
        coach.Received.ShouldHaveSingleItem();
        coach.Received.ShouldNotContain(n => Text(n, "id") == rolledBack.ToString());
    }

    [Fact]
    public async Task A_revoked_parent_account_is_no_longer_told_even_while_its_token_lives()
    {
        var parentA = await ListenAsync(_a.Token);
        // The School Admin revokes the account; the access token on the open connection is still valid for minutes.
        (await RevokeAsync(await GuardianOfAsync(_a.Student))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Admin.PutAsJsonAsync($"/api/v1/groups/{_groupX}/sessions/{SessionKey}/attendance", new
        {
            records = new[] { new { studentId = _a.Student, status = "Late" } },
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await DeliverAsync();
        await Task.Delay(StaysQuiet, Ct);
        parentA.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_user_may_hold_a_bounded_number_of_connections()
    {
        var token = Token(Ayse.Id.Value, "Coach", School.Value);
        for (var i = 0; i < 10; i++)
        {
            await ListenAsync(token);
        }

        await using var extra = Connection(Api, token);
        var closed = new TaskCompletionSource();
        extra.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await extra.StartAsync(Ct);
            await closed.Task.WaitAsync(Arrives, Ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
        {
            // Refused while starting: as good as closed right after.
        }

        extra.State.ShouldBe(HubConnectionState.Disconnected);
    }

    private string Token(Guid user, string role, Guid? tenant, DateTimeOffset? at = null) =>
        TestTokens.Create(user, role, tenant, at ?? Api.Clock.GetUtcNow());

    /// <summary>A negotiate request like the browser's WebSocket path: the token only in the query.</summary>
    private static async Task<HttpStatusCode> NegotiateAsync(HttpClient client, string? queryToken)
    {
        var query = queryToken is null ? string.Empty : $"&access_token={Uri.EscapeDataString(queryToken)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/hubs/notifications/negotiate?negotiateVersion=1{query}", UriKind.Relative));
        request.Headers.Authorization = null;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return (await client.SendAsync(request, Ct)).StatusCode;
    }

    private async Task<Guid> GuardianOfAsync(Guid student)
    {
        var detail = await GetOkAsync(Admin, $"/api/v1/students/{student}");
        return detail.GetProperty("guardians")[0].GetProperty("guardianId").GetGuid();
    }

    /// <summary>A child enrolled in the group, with a signed-in parent account.</summary>
    private async Task<(Guid Student, Guid Enrollment, string Token)> FamilyAsync(Guid group, string name, string email)
    {
        var (student, guardian) = await RegisterChildAsync(name, email, lastName: name);
        var enrolled = await EnrollAsync(student, group, "2027-01-04");
        enrolled.StatusCode.ShouldBe(HttpStatusCode.Created);
        var (client, session) = await SignedInParentAsync(guardian, email);
        client.Dispose();
        return (student, IdOf(await BodyOf(enrolled)), session.AccessToken);
    }
}

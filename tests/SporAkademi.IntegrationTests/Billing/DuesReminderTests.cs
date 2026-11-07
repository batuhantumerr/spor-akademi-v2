using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Domain.Billing;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.Infrastructure.Outbox;
using SporAkademi.Infrastructure.Persistence;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>
/// P2-F4 fee reminders (DEC-355, DEC-356): a daily per-school job records the reminders that are due and the outbox
/// e-mails them after commit, to the guardians (or an adult student). At most one reminder per enrollment, period and
/// kind, however often the job runs. The clock starts on 2027-01-14, after January's due date (the 10th).
/// </summary>
[Trait("Story", "P2-F4")]
public sealed class DuesReminderTests(DatabaseFixture database) : BillingTest(database)
{
    [Fact]
    public async Task An_unpaid_period_gets_one_overdue_reminder_however_often_the_job_runs()
    {
        var (student, _, enrollment) = await EnrolledAsync(fee: "1000.00");
        var (_, _, paidUp) = await EnrolledAsync(groupName: "U12 Futbol", firstName: "Veli");
        await RecordOkAsync(paidUp, "2027-01", "1000.00");

        (await RunJobAsync()).ShouldBe(1);
        Api.Emails.Sent.ShouldBeEmpty();
        await DeliverAsync();

        var mail = Api.Emails.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe("veli@example.com");
        mail.Template.ShouldBe("billing.dues-overdue");
        mail.Subject.ShouldBe("Ocak 2027 aidatı: ödeme tarihi geçti");
        mail.TextBody.ShouldContain("Merhaba Veli Deneme,");
        mail.TextBody.ShouldContain("Ali Deneme");
        mail.TextBody.ShouldContain("U10 Futbol grubu");
        mail.TextBody.ShouldContain("₺1.000,00");
        mail.TextBody.ShouldContain("10.01.2027");

        var reminder = (await RemindersAsync()).ShouldHaveSingleItem();
        reminder.EnrollmentId.Value.ShouldBe(enrollment);
        reminder.StudentId.Value.ShouldBe(student);
        reminder.Kind.ShouldBe(DuesReminderKind.Overdue);
        reminder.Status.ShouldBe(DuesReminderStatus.Sent);
        reminder.RecipientCount.ShouldBe(1);

        (await RunJobAsync()).ShouldBe(0);
        Api.Clock.Advance(TimeSpan.FromDays(1));
        (await RunJobAsync()).ShouldBe(0);
        await DeliverAsync();
        Api.Emails.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_period_gets_an_upcoming_reminder_before_its_due_date_and_an_overdue_one_after()
    {
        var (_, _, enrollment) = await EnrolledAsync(fee: "800.00");
        await RecordOkAsync(enrollment, "2027-01", "800.00");

        AdvanceTo(new DateTimeOffset(2027, 2, 6, 6, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(0);

        AdvanceTo(new DateTimeOffset(2027, 2, 7, 6, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(1);
        AdvanceTo(new DateTimeOffset(2027, 2, 10, 6, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(0);
        AdvanceTo(new DateTimeOffset(2027, 2, 11, 6, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(1);
        await DeliverAsync();

        Api.Emails.Sent.Select(m => m.Template).ShouldBe(["billing.dues-upcoming", "billing.dues-overdue"], ignoreOrder: true);
        Api.Emails.Sent.ShouldAllBe(m => m.Subject.StartsWith("Şubat 2027", StringComparison.Ordinal));
        (await RemindersAsync()).Select(r => r.Kind).ShouldBe([DuesReminderKind.Upcoming, DuesReminderKind.Overdue], ignoreOrder: true);
    }

    [Fact]
    public async Task A_due_paid_before_the_e_mail_goes_out_is_skipped_and_a_partial_payment_lowers_the_amount()
    {
        var (_, _, paidLater) = await EnrolledAsync(fee: "1000.00");
        var (_, _, partly) = await EnrolledAsync(groupName: "U12 Futbol", fee: "1000.00", firstName: "Can");
        (await RunJobAsync()).ShouldBe(2);

        await RecordOkAsync(paidLater, "2027-01", "1000.00");
        await RecordOkAsync(partly, "2027-01", "400.00");
        await DeliverAsync();

        Api.Emails.Sent.ShouldHaveSingleItem().TextBody.ShouldContain("₺600,00");
        var reminders = await RemindersAsync();
        reminders.Single(r => r.EnrollmentId.Value == paidLater).Status.ShouldBe(DuesReminderStatus.Skipped);
        reminders.Single(r => r.EnrollmentId.Value == paidLater).SkipReason.ShouldBe(DuesReminderSkipReason.Paid);
        reminders.Single(r => r.EnrollmentId.Value == partly).Status.ShouldBe(DuesReminderStatus.Sent);
    }

    [Fact]
    public async Task Guardians_or_an_adult_student_receive_it_and_a_minor_without_a_guardian_e_mail_is_not_recorded()
    {
        var created = await PostGroupAsync(NewGroup("Karma", capacity: 10, from: 1990, to: 2016));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var group = IdOf(await BodyOf(created));
        await DefineFeeOkAsync(group, "2027-01", "500.00");
        var adult = await RegisterRawAsync(new
        {
            firstName = "Zeynep",
            lastName = "Yetişkin",
            birthDate = "1990-03-10",
            gender = "Female",
            phone = "0533 900 00 01",
            email = "zeynep@example.com",
            emergencyContact = new { name = "Acil Kişi", phone = "0533 900 00 02", relationship = "Eş" },
        });
        var minor = await RegisterRawAsync(new
        {
            firstName = "Ece",
            lastName = "Postasiz",
            birthDate = "2016-04-01",
            gender = "Female",
            email = "ece@example.com",
            emergencyContact = new { name = "Acil Kişi", phone = "0533 900 00 03", relationship = "Teyze" },
            guardians = new[] { new { newGuardian = new { firstName = "Veli", lastName = "Postasiz", phone = "0533 900 00 04", email = (string?)null }, relationship = "Father" } },
        });
        await EnrollOkAsync(adult, group, "2027-01-04");
        await EnrollOkAsync(minor, group, "2027-01-04");

        (await RunJobAsync()).ShouldBe(1);
        await DeliverAsync();

        Api.Emails.Sent.ShouldHaveSingleItem().To.ShouldBe("zeynep@example.com");
        // A minor is never e-mailed directly, even with an own address.
        Api.Emails.To("ece@example.com").ShouldBeEmpty();
        (await RemindersAsync()).ShouldHaveSingleItem().StudentId.Value.ShouldBe(adult);
    }

    [Fact]
    public async Task Each_school_is_reminded_about_its_own_students_and_an_inactive_school_not_at_all()
    {
        await EnrolledAsync(fee: "1000.00");
        var other = await TestSchools.CreateAsync(Api, "okul-b");
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE tenancy.schools SET status = 'Deactivated' WHERE id = {other.Value}", Ct);
        }

        (await RunJobAsync()).ShouldBe(1);
        (await RemindersAsync()).ShouldAllBe(r => r.TenantId == School);

        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE tenancy.schools SET status = 'Deactivated' WHERE id = {School.Value}", Ct);
        }

        Api.Clock.Advance(TimeSpan.FromDays(40));
        (await RunJobAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("false", "2026-01-01")]
    [InlineData("true", "2027-01-11")]
    public async Task Nothing_is_sent_when_reminders_are_off_or_for_periods_due_before_go_live(string enabled, string startsOn)
    {
        await EnrolledAsync(fee: "1000.00");
        await using var host = Api.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Billing:DuesReminders:Enabled"] = enabled,
                ["Billing:DuesReminders:StartsOn"] = startsOn,
            })));

        (await RunJobAsync(host.Services)).ShouldBe(0);

        // The control: with the default test settings the same January due is reminded.
        (await RunJobAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_enrollment_that_started_after_the_due_date_gets_no_reminder_for_that_period()
    {
        await EnrolledAsync(fee: "1000.00", startDate: "2027-01-12");

        (await RunJobAsync()).ShouldBe(0);

        // February is due after the start: it is reminded as usual.
        AdvanceTo(new DateTimeOffset(2027, 2, 8, 6, 0, 0, TimeSpan.Zero));
        (await RunJobAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_failure_for_one_guardian_is_retried_without_resending_to_the_other()
    {
        var group = IdOf(await GroupAsync("Kardes", capacity: 10));
        await DefineFeeOkAsync(group, "2027-01", "900.00");
        var student = await RegisterRawAsync(new
        {
            firstName = "Deniz",
            lastName = "Iki",
            birthDate = "2015-06-01",
            gender = "Male",
            emergencyContact = new { name = "Acil Kişi", phone = "0533 910 00 01", relationship = "Teyze" },
            guardians = new[]
            {
                new { newGuardian = new { firstName = "Anne", lastName = "Iki", phone = "0533 910 00 02", email = (string?)"anne@example.com" }, relationship = "Mother" },
                new { newGuardian = new { firstName = "Baba", lastName = "Iki", phone = "0533 910 00 03", email = (string?)"baba@example.com" }, relationship = "Father" },
            },
        });
        await EnrollOkAsync(student, group, "2027-01-04");
        (await RunJobAsync()).ShouldBe(1);

        Api.Emails.FailWith = new IOException("SMTP refused one recipient");
        Api.Emails.FailWhen = m => m.To == "baba@example.com";
        await DeliverAsync();
        Api.Emails.To("anne@example.com").Count.ShouldBe(1);
        Api.Emails.To("baba@example.com").ShouldBeEmpty();

        Api.Emails.FailWith = null;
        Api.Clock.Advance(TimeSpan.FromMinutes(5));
        await DeliverAsync();

        Api.Emails.To("anne@example.com").Count.ShouldBe(1);
        Api.Emails.To("baba@example.com").Count.ShouldBe(1);
        var reminder = (await RemindersAsync()).ShouldHaveSingleItem();
        reminder.Status.ShouldBe(DuesReminderStatus.Sent);
        reminder.RecipientCount.ShouldBe(2);
    }

    private Task<int> RunJobAsync() => RunJobAsync(Api.Services);

    private static async Task<int> RunJobAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DuesReminderJob>().RunAsync(Ct);
    }

    private async Task DeliverAsync()
    {
        var processor = Api.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Reminders of every school (the test reads them as the platform, so no tenant filter applies by school).</summary>
    private async Task<List<DuesReminder>> RemindersAsync()
    {
        var all = new List<DuesReminder>();
        foreach (var school in await SchoolIdsAsync())
        {
            await using var scope = Api.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<BackgroundTenantContext>().Enter(school);
            all.AddRange(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<DuesReminder>().AsNoTracking().ToListAsync(Ct));
        }

        return all;
    }

    private async Task<List<Guid>> SchoolIdsAsync()
    {
        await using var scope = Api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM tenancy.schools")
            .ToListAsync(Ct);
    }

    private async Task<Guid> RegisterRawAsync(object student)
    {
        var response = await Admin.PostAsJsonAsync("/api/v1/students", student, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return IdOf(await BodyOf(response));
    }
}

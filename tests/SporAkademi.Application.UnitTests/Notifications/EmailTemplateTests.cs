using SporAkademi.Application.Billing;
using SporAkademi.Application.Identity.Passwords;
using SporAkademi.Application.Students.Contracts;
using SporAkademi.Domain.Billing;
using SporAkademi.Domain.Enrollments;
using SporAkademi.Domain.Groups;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Notifications;

/// <summary>P2-F4 (DEC-350, DEC-351): Turkish plain text plus simple HTML, values encoded, no tracking, no temporary password.</summary>
[Trait("Story", "P2-F4")]
public sealed class EmailTemplateTests
{
    private static readonly Uri Link = new("https://app.example.com/reset-password#token=abc_DEF-123");
    private static readonly Uri Forgot = new("https://app.example.com/forgot-password");

    [Fact]
    public void The_welcome_e_mail_carries_a_set_password_link_and_no_password()
    {
        var user = User.Create(EntityId.New<SchoolId>(), Email.Create("coach@example.com").Value, "hash-of-temporary", UserRole.Coach, "Ayşe", "Yılmaz", null).Value;

        var message = IdentityEmails.Welcome(user, "Kadıköy <Spor>", Link, Forgot);

        message.Template.ShouldBe("identity.welcome");
        message.To.ShouldBe("coach@example.com");
        message.Subject.ShouldBe("Spor Akademi hesabınız açıldı");
        message.TextBody.ShouldContain("Merhaba Ayşe Yılmaz,");
        message.TextBody.ShouldContain(Link.AbsoluteUri);
        message.TextBody.ShouldContain("24 saat");
        message.TextBody.ShouldContain("antrenör");
        message.TextBody.ShouldNotContain("şifreniz:", Case.Insensitive);
        message.TextBody.ShouldNotContain("hash-of-temporary");
        message.HtmlBody.ShouldContain("Kadıköy &lt;Spor&gt;");
        message.HtmlBody.ShouldNotContain("<Spor>");
        message.HtmlBody.ShouldContain("href=\"https://app.example.com/reset-password#token=abc_DEF-123\"");
    }

    [Fact]
    public void The_reset_e_mail_states_the_lifetime_and_that_sessions_end()
    {
        var user = User.Create(null, Email.Create("platform@example.com").Value, "h", UserRole.PlatformAdmin, "Deniz", "Kaya", null).Value;

        var message = IdentityEmails.PasswordReset(user, Link);

        message.Template.ShouldBe("identity.password-reset");
        message.TextBody.ShouldContain("30 dakika");
        message.TextBody.ShouldContain("oturumlarınız kapanır");
        message.TextBody.ShouldContain(Link.AbsoluteUri);
    }

    [Fact]
    [Trait("Story", "P3-02")]
    public void The_parent_invitation_names_the_school_and_login_and_says_the_link_lives_three_days()
    {
        var parent = User.InviteParent(EntityId.New<SchoolId>(), Email.Create("veli@example.com").Value, "unusable", "Ayşe", "Deneme", null, DateTimeOffset.UnixEpoch).Value;

        var message = IdentityEmails.ParentInvitation(parent, "Kadıköy <Spor>", Link);

        message.Template.ShouldBe("identity.parent-invitation");
        message.To.ShouldBe("veli@example.com");
        message.Subject.ShouldBe("Spor Akademi veli hesabı daveti");
        message.TextBody.ShouldContain("Merhaba Ayşe Deneme,");
        message.TextBody.ShouldContain("veli@example.com");
        message.TextBody.ShouldContain("72 saat");
        message.TextBody.ShouldContain(Link.AbsoluteUri);
        message.TextBody.ShouldNotContain("unusable");
        message.HtmlBody.ShouldContain("Kadıköy &lt;Spor&gt;");
    }

    [Theory]
    [InlineData(DuesReminderKind.Upcoming, "billing.dues-upcoming", "Ocak 2027 aidatı: son ödeme tarihi yaklaşıyor")]
    [InlineData(DuesReminderKind.Overdue, "billing.dues-overdue", "Ocak 2027 aidatı: ödeme tarihi geçti")]
    public void Fee_reminders_name_the_student_group_month_amount_and_due_date(DuesReminderKind kind, string template, string subject)
    {
        var reminder = DuesReminder.Schedule(
            EntityId.New<SchoolId>(),
            EntityId.New<EnrollmentId>(),
            EntityId.New<StudentId>(),
            EntityId.New<GroupId>(),
            YearMonth.Create(2027, 1).Value,
            kind,
            new DateOnly(2027, 1, 10),
            Money.Try(1250m));
        var recipient = new ReminderRecipient(Guid.CreateVersion7(), "Elif", "Demir", "veli@example.com");
        var student = new ReminderContacts(Guid.CreateVersion7(), "Can", "Demir", [recipient]);

        var message = DuesReminderEmails.Compose(reminder, 1250m, recipient, student, "Minikler", "Kadıköy Spor");

        message.Template.ShouldBe(template);
        message.Subject.ShouldBe(subject);
        message.To.ShouldBe("veli@example.com");
        message.TextBody.ShouldContain("Merhaba Elif Demir,");
        message.TextBody.ShouldContain("Can Demir");
        message.TextBody.ShouldContain("Minikler grubu");
        message.TextBody.ShouldContain("₺1.250,00");
        message.TextBody.ShouldContain("10.01.2027");
    }

    [Fact]
    public void No_e_mail_loads_anything_from_outside()
    {
        var user = User.Create(EntityId.New<SchoolId>(), Email.Create("coach@example.com").Value, "h", UserRole.Coach, "A", "B", null).Value;
        string[] htmlBodies =
        [
            IdentityEmails.Welcome(user, "Okul", Link, Forgot).HtmlBody,
            IdentityEmails.PasswordReset(user, Link).HtmlBody,
        ];

        foreach (var html in htmlBodies)
        {
            html.ShouldNotContain("<img", Case.Insensitive);
            html.ShouldNotContain("<link", Case.Insensitive);
            html.ShouldNotContain("<script", Case.Insensitive);
            html.ShouldNotContain("url(", Case.Insensitive);
            html.ShouldNotContain("src=", Case.Insensitive);
        }
    }
}

using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Parents;

/// <summary>P3-13 (DEC-423): after a refund commits, the child's guardians are e-mailed through the outbox, without the reason.</summary>
[Trait("Story", "US-23")]
[Trait("Roadmap", "P3-13")]
public sealed class RefundNotificationTests(DatabaseFixture database) : ParentTest(database)
{
    [Fact]
    public async Task The_guardian_is_e_mailed_once_after_commit_without_the_schools_reason()
    {
        var group = IdOf(await GroupAsync("U10 Futbol"));
        await DefineFeeOkAsync(group, "2027-01", "1000.00");
        var (student, _) = await RegisterChildAsync("Deniz", "deniz.veli@example.com", lastName: "Iade");
        var enrollment = IdOf(await EnrollOkAsync(student, group, "2027-01-04"));
        var (_, payment) = await PayOnlineAsync(enrollment);
        await DeliverAsync();
        Api.Emails.Clear();

        await RefundOkAsync(payment, "Iade ailesi taşındı");
        Api.Emails.Sent.ShouldBeEmpty(); // nothing leaves inside the request
        await DeliverAsync();
        await DeliverAsync();

        var mail = Api.Emails.To("deniz.veli@example.com").ShouldHaveSingleItem();
        mail.Template.ShouldBe("billing.payment-refunded");
        mail.Subject.ShouldBe("Ocak 2027 aidatı ödemeniz iade edildi");
        mail.TextBody.ShouldContain("Deniz Iade");
        mail.TextBody.ShouldContain("₺1.000,00");
        mail.TextBody.ShouldNotContain("taşındı");
        mail.HtmlBody.ShouldNotContain("taşındı");
    }
}

using Microsoft.Extensions.DependencyInjection;
using SporAkademi.Infrastructure.Jobs;
using SporAkademi.IntegrationTests.Infrastructure;

namespace SporAkademi.IntegrationTests.Billing;

/// <summary>P2-F1 (DEC-343): the recurring job credits payments whose webhook never arrived and expires old links.</summary>
[Trait("Roadmap", "P2-F1")]
public sealed class OnlinePaymentReconcileJobTests(DatabaseFixture database) : OnlinePaymentTest(database)
{
    private Task<int> RunJobAsync() =>
        InScopeAsync(services => services.GetRequiredService<OnlinePaymentReconcileJob>().RunAsync(Ct));

    [Fact]
    public async Task A_paid_attempt_whose_webhook_was_lost_is_credited_once()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        Fake.Complete(TokenOf(started), succeed: true).ShouldBeTrue();

        // Too recent: the webhook may still come.
        (await RunJobAsync()).ShouldBe(0);
        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunJobAsync()).ShouldBe(1);
        (await RunJobAsync()).ShouldBe(0);

        (await OnlinePaymentsRecordedAsync()).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.online_payments WHERE status = 'Succeeded'")).ShouldBe(1);
    }

    [Fact]
    public async Task An_unpaid_attempt_stays_pending_until_its_link_expires()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        await StartOnlineOkAsync(enrollment);

        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunJobAsync()).ShouldBe(0);
        (await CountAsync("SELECT count(*) FROM billing.online_payments WHERE status = 'Pending'")).ShouldBe(1);

        Api.Clock.Advance(TimeSpan.FromMinutes(20));
        (await RunJobAsync()).ShouldBe(1);
        (await CountAsync("SELECT count(*) FROM billing.online_payments WHERE status = 'Expired'")).ShouldBe(1);
        (await OnlinePaymentsRecordedAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_declined_attempt_is_marked_failed()
    {
        var (_, _, enrollment) = await EnrolledAsync();
        var started = await StartOnlineOkAsync(enrollment);
        Fake.Complete(TokenOf(started), succeed: false).ShouldBeTrue();

        Api.Clock.Advance(TimeSpan.FromMinutes(11));
        (await RunJobAsync()).ShouldBe(1);

        (await CountAsync("SELECT count(*) FROM billing.online_payments WHERE status = 'Failed'")).ShouldBe(1);
    }
}

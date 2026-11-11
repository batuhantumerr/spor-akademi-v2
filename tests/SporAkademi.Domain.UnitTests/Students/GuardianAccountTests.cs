using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Students;

/// <summary>P3-01 (DEC-382): a guardian is linked to at most one parent account, and keeps it.</summary>
[Trait("Story", "P3-01")]
public sealed class GuardianAccountTests
{
    private static Guardian NewGuardian() =>
        Guardian.Create(EntityId.New<SchoolId>(), "Ayşe", "Deneme", PhoneNumber.Create("0532 111 22 33").Value, Email.Create("veli@example.com").Value, null).Value;

    [Fact]
    public void A_new_guardian_has_no_parent_account()
    {
        NewGuardian().AccountUserId.ShouldBeNull();
    }

    [Fact]
    public void Linking_the_same_account_again_is_allowed()
    {
        var guardian = NewGuardian();
        var account = Guid.CreateVersion7();

        guardian.LinkAccount(account);
        guardian.LinkAccount(account);

        guardian.AccountUserId.ShouldBe(account);
    }

    [Fact]
    public void A_guardian_never_moves_to_another_account()
    {
        var guardian = NewGuardian();
        guardian.LinkAccount(Guid.CreateVersion7());

        Should.Throw<InvalidOperationException>(() => guardian.LinkAccount(Guid.CreateVersion7()));
        Should.Throw<InvalidOperationException>(() => NewGuardian().LinkAccount(Guid.Empty));
    }
}

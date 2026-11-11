using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Identity;

/// <summary>P3-02 (DEC-383): a parent account is opened by an invitation and has no usable password until it is accepted.</summary>
[Trait("Story", "P3-02")]
public sealed class ParentAccountTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SchoolId School = SchoolId.From(Guid.CreateVersion7());

    private static User Invite(string email = "veli@example.com") =>
        User.InviteParent(School, Email.Create(email).Value, "unusable-hash", "Ayşe", "Deneme", null, Now).Value;

    [Fact]
    public void An_invited_parent_belongs_to_the_school_and_must_set_a_password_first()
    {
        var parent = Invite();

        parent.Role.ShouldBe(UserRole.Parent);
        parent.TenantId.ShouldBe(School);
        parent.Status.ShouldBe(UserStatus.Active);
        parent.MustChangePassword.ShouldBeTrue();
    }

    [Fact]
    public void An_invitation_raises_parent_invited_and_no_admin_welcome()
    {
        var parent = Invite();

        parent.DomainEvents.OfType<ParentInvited>().ShouldHaveSingleItem().ShouldBe(new ParentInvited(parent.Id, School));
        parent.DomainEvents.OfType<UserCreated>().ShouldBeEmpty();
    }

    [Fact]
    public void A_parent_account_cannot_be_created_with_a_temporary_password()
    {
        Should.Throw<ArgumentException>(() =>
            User.Create(School, Email.Create("veli@example.com").Value, "hash", UserRole.Parent, "Ayşe", "Deneme", null));
    }

    [Fact]
    public void Invitation_links_live_seventy_two_hours()
    {
        var parent = Invite();

        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now).Value.ExpiresAt.ShouldBe(Now.AddHours(72));
        parent.InvitationExpiresAt.ShouldBe(Now.AddHours(72));
    }

    [Fact]
    public void Accepting_the_invitation_sets_the_first_password()
    {
        var parent = Invite();
        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now);

        parent.SetPasswordWithToken("davet", "own-hash", Now.AddHours(71)).IsSuccess.ShouldBeTrue();

        parent.PasswordHash.ShouldBe("own-hash");
        parent.MustChangePassword.ShouldBeFalse();
        parent.InvitationExpiresAt.ShouldBeNull();
    }

    [Fact]
    public void An_expired_invitation_link_is_refused()
    {
        var parent = Invite();
        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now);

        parent.SetPasswordWithToken("davet", "own-hash", Now.AddHours(72)).Error.ShouldBe(AuthErrors.ResetTokenInvalid);
    }

    [Fact]
    public void Inviting_again_voids_the_earlier_link_and_raises_a_new_invitation()
    {
        var parent = Invite();
        parent.IssuePasswordToken("ilk", PasswordTokenPurpose.Invitation, Now);
        parent.ClearDomainEvents();

        parent.ReinviteParent(Email.Create("veli@example.com").Value, "new-unusable", "Ayşe", "Deneme", null, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        parent.PasswordTokens.ShouldBeEmpty();
        parent.SetPasswordWithToken("ilk", "own-hash", Now.AddHours(2)).Error.ShouldBe(AuthErrors.ResetTokenInvalid);
        parent.DomainEvents.OfType<ParentInvited>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Inviting_a_closed_account_again_reopens_it_with_the_current_address_and_ends_old_sessions()
    {
        var parent = Invite();
        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now);
        parent.SetPasswordWithToken("davet", "own-hash", Now);
        var session = parent.CompleteLogin("r1", Now).Value;
        parent.Deactivate(Now).IsSuccess.ShouldBeTrue();

        parent.ReinviteParent(Email.Create("yeni@example.com").Value, "new-unusable", "Ayşe", "Yılmaz", null, Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        parent.Status.ShouldBe(UserStatus.Active);
        parent.MustChangePassword.ShouldBeTrue();
        parent.PasswordHash.ShouldBe("new-unusable");
        parent.Email.Value.ShouldBe("yeni@example.com");
        parent.LastName.ShouldBe("Yılmaz");
        session.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public void A_parent_who_set_a_password_is_not_invited_again()
    {
        var parent = Invite();
        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now);
        parent.SetPasswordWithToken("davet", "own-hash", Now);

        parent.ReinviteParent(parent.Email, "new-unusable", "Ayşe", "Deneme", null, Now).Error.ShouldBe(ParentAccountErrors.AlreadyActive);
        parent.PasswordHash.ShouldBe("own-hash");
    }

    [Fact]
    [Trait("Story", "P3-01")]
    public void Erasing_a_parent_closes_it_and_replaces_its_personal_data()
    {
        var parent = User.InviteParent(School, Email.Create("veli@example.com").Value, "unusable-hash", "Ayşe", "Deneme", PhoneNumber.Create("0532 111 22 33").Value, Now).Value;
        parent.IssuePasswordToken("davet", PasswordTokenPurpose.Invitation, Now);
        parent.SetPasswordWithToken("davet", "own-hash", Now);
        var session = parent.CompleteLogin("r1", Now).Value;

        parent.EraseParent(Now.AddDays(1));

        parent.Status.ShouldBe(UserStatus.Deactivated);
        session.RevokedAt.ShouldNotBeNull();
        parent.FirstName.ShouldBe(User.ErasedParentFirstName);
        parent.LastName.ShouldBe(User.ErasedParentLastName);
        parent.Phone.ShouldBeNull();
        parent.Email.Value.ShouldNotContain("veli@example.com");
        parent.Email.Value.ShouldEndWith("@silinmis.invalid");
        Should.Throw<InvalidOperationException>(() => UserTests.NewCoach().EraseParent(Now));
    }

    [Fact]
    public void A_new_invitation_within_two_minutes_is_refused()
    {
        var parent = Invite();

        parent.ReinviteParent(parent.Email, "h2", "Ayşe", "Deneme", null, Now.AddSeconds(119)).Error.ShouldBe(ParentAccountErrors.InvitationTooSoon);
        parent.ReinviteParent(parent.Email, "h2", "Ayşe", "Deneme", null, Now.AddMinutes(2)).IsSuccess.ShouldBeTrue();
        parent.InvitedAt.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void Only_parent_accounts_are_invited_again()
    {
        var coach = UserTests.NewCoach();

        Should.Throw<InvalidOperationException>(() => coach.ReinviteParent(coach.Email, "hash", "Ayşe", "Deneme", null, Now));
    }
}

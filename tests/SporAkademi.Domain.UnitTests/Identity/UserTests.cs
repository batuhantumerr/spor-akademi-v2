using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Domain.UnitTests.Identity;

public sealed class UserTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SchoolId School = EntityId.New<SchoolId>();

    internal static User NewCoach(SchoolId? school = null) =>
        User.Create(school ?? School, Email.Create("coach@example.com").Value, "hash", UserRole.Coach, "Ayşe", "Yılmaz", null).Value;

    [Fact]
    public void New_user_is_active_and_must_change_the_temporary_password()
    {
        var user = NewCoach();

        user.Status.ShouldBe(UserStatus.Active);
        user.MustChangePassword.ShouldBeTrue();
        user.TenantId.ShouldBe(School);
        user.Id.Value.Version.ShouldBe(7);
        user.FailedLoginCount.ShouldBe(0);
        user.RefreshTokens.ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_platform_admin_has_no_school()
    {
        var email = Email.Create("a@example.com").Value;

        User.Create(null, email, "hash", UserRole.PlatformAdmin, "A", "B", null).IsSuccess.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => User.Create(School, email, "hash", UserRole.PlatformAdmin, "A", "B", null));
        Should.Throw<ArgumentException>(() => User.Create(null, email, "hash", UserRole.SchoolAdmin, "A", "B", null));
        Should.Throw<ArgumentException>(() => User.Create(null, email, "hash", UserRole.Coach, "A", "B", null));
    }

    [Fact]
    [Trait("Story", "US-08")]
    public void Profile_update_trims_names_and_sets_the_phone()
    {
        var user = NewCoach();
        var phone = PhoneNumber.Create("0532 123 45 67").Value;

        user.UpdateProfile("  Elif ", " Demir ", phone).IsSuccess.ShouldBeTrue();

        user.FirstName.ShouldBe("Elif");
        user.LastName.ShouldBe("Demir");
        user.Phone.ShouldBe(phone);
        user.UpdateProfile("Elif", "Demir", null).IsSuccess.ShouldBeTrue();
        user.Phone.ShouldBeNull();
        user.UpdateProfile("", "Demir", null).Error.ShouldBe(CommonErrors.FieldRequired);
        user.FirstName.ShouldBe("Elif");
    }

    [Fact]
    public void Names_are_required_and_bounded()
    {
        var email = Email.Create("a@example.com").Value;

        User.Create(School, email, "hash", UserRole.Coach, " ", "B", null).Error.ShouldBe(CommonErrors.FieldRequired);
        User.Create(School, email, "hash", UserRole.Coach, "A", new string('b', 101), null).Error.ShouldBe(CommonErrors.FieldTooLong);
    }
}

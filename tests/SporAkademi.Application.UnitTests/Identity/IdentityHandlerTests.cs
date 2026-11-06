using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SporAkademi.Application.Abstractions.Authentication;
using SporAkademi.Application.Identity;
using SporAkademi.Application.Identity.Login;
using SporAkademi.Application.Identity.Passwords;
using SporAkademi.Application.Tenancy.Contracts;
using SporAkademi.Domain.Identity;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Tenancy;

namespace SporAkademi.Application.UnitTests.Identity;

public sealed class IdentityHandlerTests
{
    private static readonly SchoolId School = EntityId.New<SchoolId>();

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IRefreshTokenFactory _refreshTokens = Substitute.For<IRefreshTokenFactory>();
    private readonly IAccessTokenIssuer _accessTokens = Substitute.For<IAccessTokenIssuer>();
    private readonly ITenancyModule _tenancy = Substitute.For<ITenancyModule>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public IdentityHandlerTests()
    {
        _hasher.Hash(Arg.Any<string>()).Returns(c => "hash:" + c.Arg<string>());
        _hasher.Verify(Arg.Any<string>(), Arg.Any<string>())
            .Returns(c => c.ArgAt<string>(0) == "hash:" + c.ArgAt<string>(1) ? PasswordVerification.Success : PasswordVerification.Failed);
        _refreshTokens.Create().Returns(new NewRefreshToken("token", "token-hash"));
        _accessTokens.Issue(Arg.Any<User>(), Arg.Any<DateTimeOffset>()).Returns(new AccessToken("jwt", _clock.GetUtcNow().AddMinutes(15)));
        _tenancy.IsSchoolActiveAsync(Arg.Any<SchoolId>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private User Coach(SchoolId? school = null)
    {
        var user = User.Create(school ?? School, Email.Create("coach@example.com").Value, "hash:Gecici-Sifre-1", UserRole.Coach, "Ayşe", "Yılmaz", null).Value;
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    private LoginHandler Login() => new(_users, _hasher, _refreshTokens, _accessTokens, _tenancy, _clock);

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Unknown_email_answers_like_a_wrong_password_and_spends_hashing_time()
    {
        var result = await Login().Handle(new LoginCommand("nobody@example.com", "whatever-password"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
        _hasher.Received(1).SpendVerificationTime("whatever-password");
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Wrong_password_is_counted()
    {
        var user = Coach();

        var result = await Login().Handle(new LoginCommand("COACH@example.com", "wrong-password"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
        user.FailedLoginCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Inactive_school_blocks_login_only_after_the_password_is_verified()
    {
        Coach();
        _tenancy.IsSchoolActiveAsync(School, Arg.Any<CancellationToken>()).Returns(false);

        (await Login().Handle(new LoginCommand("coach@example.com", "wrong-password"), CancellationToken.None))
            .Error.ShouldBe(AuthErrors.InvalidCredentials);
        (await Login().Handle(new LoginCommand("coach@example.com", "Gecici-Sifre-1"), CancellationToken.None))
            .Error.ShouldBe(AuthErrors.AccountInactive);
    }

    [Fact]
    [Trait("Story", "US-03")]
    public async Task Outdated_hashes_are_upgraded_on_login()
    {
        var user = Coach();
        _hasher.Verify(user.PasswordHash, "Gecici-Sifre-1").Returns(PasswordVerification.SuccessRehashNeeded);

        var result = await Login().Handle(new LoginCommand("coach@example.com", "Gecici-Sifre-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        user.PasswordHash.ShouldBe("hash:Gecici-Sifre-1");
        _hasher.Received(1).Hash("Gecici-Sifre-1");
    }

    [Fact]
    [Trait("Story", "US-05")]
    public async Task Wrong_current_password_changes_nothing_but_counts_towards_the_lockout()
    {
        var user = Coach();
        var handler = new ChangePasswordHandler(_users, _hasher, _refreshTokens, _accessTokens, _clock);

        var result = await handler.Handle(new ChangePasswordCommand(user.Id.Value, "wrong-password", "Yeni-Sifre-2027"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.CurrentPasswordIncorrect);
        user.MustChangePassword.ShouldBeTrue();
        user.FailedLoginCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Story", "US-05")]
    public async Task New_password_equal_to_the_current_one_is_rejected()
    {
        var user = Coach();
        var handler = new ChangePasswordHandler(_users, _hasher, _refreshTokens, _accessTokens, _clock);

        var result = await handler.Handle(new ChangePasswordCommand(user.Id.Value, "Gecici-Sifre-1", "Gecici-Sifre-1"), CancellationToken.None);

        result.Error.Code.ShouldBe("Validation.Failed");
        ((IReadOnlyDictionary<string, string[]>)result.Error.Arguments["errors"]!)["newPassword"].ShouldBe(["Auth.PasswordUnchanged"]);
        user.MustChangePassword.ShouldBeTrue();
    }

    [Theory]
    [Trait("Story", "US-05")]
    [InlineData(Role.SchoolAdmin, true, true)]
    [InlineData(Role.SchoolAdmin, false, false)]
    [InlineData(Role.Coach, true, false)]
    [InlineData(Role.PlatformAdmin, true, false)]
    public async Task Coach_passwords_are_reset_only_by_their_own_school_admin(Role callerRole, bool sameSchool, bool allowed)
    {
        var user = Coach();
        _currentUser.Role.Returns(callerRole);
        _currentUser.TenantId.Returns(sameSchool ? School.Value : Guid.CreateVersion7());
        var handler = new ResetUserPasswordHandler(_users, _hasher, _currentUser, _clock);

        var result = await handler.Handle(new ResetUserPasswordCommand(user.Id.Value, School.Value), CancellationToken.None);

        if (allowed)
        {
            result.Value.TemporaryPassword.Length.ShouldBe(TemporaryPassword.Length);
            user.PasswordHash.ShouldBe("hash:" + result.Value.TemporaryPassword);
            user.MustChangePassword.ShouldBeTrue();
        }
        else
        {
            result.Error.ShouldBe(UserErrors.NotFound);
            user.PasswordHash.ShouldBe("hash:Gecici-Sifre-1");
        }
    }

    [Theory]
    [Trait("Story", "US-05")]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Platform_admin_resets_school_admins_of_the_given_school(bool rightSchool, bool allowed)
    {
        var admin = User.Create(School, Email.Create("admin@example.com").Value, "hash:x", UserRole.SchoolAdmin, "A", "B", null).Value;
        _users.GetByIdAsync(admin.Id, Arg.Any<CancellationToken>()).Returns(admin);
        _currentUser.Role.Returns(Role.PlatformAdmin);
        _currentUser.TenantId.Returns((Guid?)null);
        var handler = new ResetUserPasswordHandler(_users, _hasher, _currentUser, _clock);

        var result = await handler.Handle(new ResetUserPasswordCommand(admin.Id.Value, rightSchool ? School.Value : Guid.CreateVersion7()), CancellationToken.None);

        result.IsSuccess.ShouldBe(allowed);
    }
}

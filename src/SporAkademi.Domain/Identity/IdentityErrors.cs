using SporAkademi.Domain.SharedKernel;

namespace SporAkademi.Domain.Identity;

/// <summary>Authentication errors (appendix E, Identity).</summary>
public static class AuthErrors
{
    /// <summary>Unknown e-mail and wrong password alike, so the response never reveals whether an account exists.</summary>
    public static readonly Error InvalidCredentials = Error.Unauthorized("Auth.InvalidCredentials", "E-mail or password is wrong.");
    public static readonly Error AccountLocked = Error.Unauthorized("Auth.AccountLocked", "Too many failed attempts; the account is locked.");
    public static readonly Error AccountInactive = Error.Unauthorized("Auth.AccountInactive", "The account or its school is not active.");
    public static readonly Error SessionExpired = Error.Unauthorized("Auth.SessionExpired", "The session has expired.");
    public static readonly Error PasswordChangeRequired = Error.Forbidden("Auth.PasswordChangeRequired", "The password must be changed first.");
    public static readonly Error CurrentPasswordIncorrect = Error.BusinessRule("Auth.CurrentPasswordIncorrect", "The current password is wrong.");
    public static readonly Error PasswordTooWeak = Error.Validation("Auth.PasswordTooWeak", "The password does not meet the policy.");
    public static readonly Error PasswordUnchanged = Error.Validation("Auth.PasswordUnchanged", "The new password must differ from the current one.");

    /// <summary>P2-F4: an unknown, expired or used set-password link; never says which.</summary>
    public static readonly Error ResetTokenInvalid = Error.BusinessRule("Auth.ResetTokenInvalid", "The set-password link is invalid or has expired.");
}

public static class UserErrors
{
    public static readonly Error EmailTaken = Error.Conflict("User.EmailTaken", "The e-mail address is already used.");
    public static readonly Error NotFound = Error.NotFound("User.NotFound", "User not found.");
    public static readonly Error AlreadyInactive = Error.BusinessRule("User.AlreadyInactive", "The user is already inactive.");
}

/// <summary>Parent accounts (P3-02, DEC-382).</summary>
public static class ParentAccountErrors
{
    /// <summary>The parent already set a password; a forgotten one is reset with "Şifremi unuttum", not a new invitation.</summary>
    public static readonly Error AlreadyActive = Error.BusinessRule("ParentAccount.AlreadyActive", "The parent account is already active.");

    /// <summary>DEC-382: another invitation within <see cref="User.InvitationCooldown"/>; against mail bombing a mailbox.</summary>
    public static readonly Error InvitationTooSoon = Error.BusinessRule("ParentAccount.InvitationTooSoon", "The guardian was invited moments ago; try again in two minutes.");

    public static readonly Error NotInvited = Error.BusinessRule("ParentAccount.NotInvited", "The guardian has no parent account.");
}

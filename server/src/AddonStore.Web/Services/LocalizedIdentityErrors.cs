using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;

namespace AddonStore.Web.Services;

/// <summary>
/// Identity's messages (password rules, invalid or taken address) in the visitor's
/// language; ASP.NET Core Identity itself only speaks English.
/// </summary>
public sealed class LocalizedIdentityErrors(IStringLocalizer<SharedResource> L) : IdentityErrorDescriber
{
    private static IdentityError E(string code, string text) => new() { Code = code, Description = text };

    public override IdentityError DefaultError() => E(nameof(DefaultError), L["An unknown failure has occurred."]);
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), L["Passwords must be at least {0} characters.", length]);
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), L["Passwords must have at least one digit ('0'-'9')."]);
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), L["Passwords must have at least one lowercase letter ('a'-'z')."]);
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), L["Passwords must have at least one uppercase letter ('A'-'Z')."]);
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), L["Email '{0}' is invalid.", email ?? ""]);
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), L["Email '{0}' is invalid.", userName ?? ""]);
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), L["Email '{0}' is already taken.", email]);
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), L["Email '{0}' is already taken.", userName]);

    /// <summary>
    /// Server-side check of the account form fields (the browser limits can be bypassed):
    /// null when fine, else the message (a resx key).
    /// </summary>
    public static string? CheckAccountInput(string? name, string? email, string? password, string? reason = null) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
        (email is not null && (string.IsNullOrWhiteSpace(email) || email.Length > 200)) ||
        string.IsNullOrEmpty(password) || password.Length > 200 || (reason?.Length ?? 0) > 500
            ? "Fill in name, email address and password (name up to 100, email address up to 200, reason up to 500 characters)."
            : null;
}

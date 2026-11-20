using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Auth;

/// <summary>
/// The ASP.NET Identity rules of the store in one place, used by Program.cs and by the tests, so a
/// test of "no second account with the same address" runs against exactly the production rules.
/// </summary>
public static class IdentitySetup
{
    public static void Apply(IdentityOptions o)
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
        // one account per address, compared on the normalized (upper-case) form: every way an account
        // is created (registration, invitation, admin, setup, Sign in with Microsoft) relies on it
        o.User.RequireUniqueEmail = true;
    }
}

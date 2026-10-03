using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Account;

public class LoginModel : PageModel
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;

    public string? Message { get; private set; }
    public string MessageKind { get; private set; } = "warn";

    public LoginModel(SignInManager<AppUser> signIn, UserManager<AppUser> users)
    {
        _signIn = signIn; _users = users;
    }

    public void OnGet(string? registered)
    {
        if (registered == "1")
        {
            Message = "Your access request was submitted. You will receive an email once an admin approves it.";
            MessageKind = "ok";
        }
    }

    public async Task<IActionResult> OnPostAsync(string email, string password)
    {
        var user = await _users.FindByEmailAsync(email);
        if (user is null)
        {
            Message = "Sign-in failed. Check email and password.";
            MessageKind = "error";
            return Page();
        }
        switch (user.Status)
        {
            case UserStatus.Pending:
                Message = "Your access request is still awaiting admin approval.";
                return Page();
            case UserStatus.Rejected:
            case UserStatus.Disabled:
                Message = "This account is not active. Contact an administrator.";
                MessageKind = "error";
                return Page();
        }
        var result = await _signIn.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            Message = result.IsLockedOut
                ? "Too many failed attempts; the account is temporarily locked."
                : "Sign-in failed. Check email and password.";
            MessageKind = "error";
            return Page();
        }
        return RedirectToPage("/Dashboard");
    }
}

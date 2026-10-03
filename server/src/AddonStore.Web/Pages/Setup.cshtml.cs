using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

public class SetupModel : PageModel
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public string? Error { get; private set; }

    public SetupModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, AuditService audit)
    {
        _users = users; _signIn = signIn; _db = db; _audit = audit;
    }

    public async Task<IActionResult> OnGetAsync() =>
        await _db.Users.AnyAsync() ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync(string name, string email, string password)
    {
        if (await _db.Users.AnyAsync()) return RedirectToPage("/Index");

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = name,
            Status = UserStatus.Active
        };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Error = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }
        await _users.AddToRoleAsync(user, "Admin");
        await _audit.LogAsync(name, "setup.admin-created", email);
        await _signIn.SignInAsync(user, isPersistent: true);
        return RedirectToPage("/Dashboard");
    }
}

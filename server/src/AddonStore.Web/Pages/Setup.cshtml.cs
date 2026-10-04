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
    private readonly SetupGate _gate;

    public string? Error { get; private set; }
    public bool TokenError { get; private set; }

    public SetupModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, AuditService audit, SetupGate gate)
    {
        _users = users; _signIn = signIn; _db = db; _audit = audit; _gate = gate;
    }

    public async Task<IActionResult> OnGetAsync() =>
        await _db.Users.AnyAsync() ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync(string name, string email, string password, string? setupToken)
    {
        // one at a time: "no account yet" and creating the first one must not interleave
        await _gate.Lock.WaitAsync();
        try { return await CreateFirstAdminAsync(name, email, password, setupToken); }
        finally { _gate.Lock.Release(); }
    }

    private async Task<IActionResult> CreateFirstAdminAsync(string name, string email, string password, string? setupToken)
    {
        if (await _db.Users.AnyAsync()) return RedirectToPage("/Index");
        if (!_gate.Check(setupToken))
        {
            TokenError = true;
            return Page();
        }

        if (AddonStore.Web.Services.LocalizedIdentityErrors.CheckAccountInput(name, email, password) is { } bad)
        {
            Error = bad;
            return Page();
        }
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
        _gate.Complete();
        await _audit.LogAsync(name, "setup.admin-created", email);
        await _signIn.SignInAsync(user, isPersistent: true);
        return RedirectToPage("/Dashboard");
    }
}

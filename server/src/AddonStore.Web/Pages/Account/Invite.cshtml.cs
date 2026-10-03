using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Account;

public class InviteModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AuditService _audit;

    public string? Error { get; private set; }
    public string Email { get; private set; } = "";
    public string Role { get; private set; } = SchemaUpgrade.DefaultRole;
    public string Token { get; private set; } = "";

    public InviteModel(AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn, AuditService audit)
    {
        _db = db; _users = users; _signIn = signIn; _audit = audit;
    }

    private async Task<Invite?> FindAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("ppiv_")) return null;
        var hash = TokenService.Hash(token);
        var cutoff = DateTime.UtcNow.AddDays(-7);
        return await _db.Invites.FirstOrDefaultAsync(i =>
            i.TokenHash == hash && i.AcceptedAt == null && i.CreatedAt > cutoff);
    }

    public async Task OnGetAsync(string? token)
    {
        var invite = await FindAsync(token);
        if (invite is null) { Error = "This invitation link is invalid or was already used."; return; }
        Email = invite.Email;
        Role = invite.Role;
        Token = token!;
    }

    public async Task<IActionResult> OnPostAsync(string token, string name, string password)
    {
        var invite = await FindAsync(token);
        if (invite is null) { Error = "This invitation link is invalid or was already used."; return Page(); }

        var user = new AppUser
        {
            UserName = invite.Email,
            Email = invite.Email,
            DisplayName = name,
            Status = UserStatus.Active
        };
        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Email = invite.Email; Role = invite.Role; Token = token;
            Error = string.Join(" ", result.Errors.Select(e => e.Description));
            return Page();
        }
        await _users.AddToRoleAsync(user, invite.Role);
        invite.AcceptedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(name, "invite.accepted", invite.Email, $"role {invite.Role}");
        await _signIn.SignInAsync(user, isPersistent: true);
        return RedirectToPage("/Dashboard");
    }
}

using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

public class ProfileModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly TokenService _tokens;
    private readonly AuditService _audit;

    public List<ApiToken> Tokens { get; private set; } = new();
    public string? NewToken { get; private set; }

    public ProfileModel(AppDbContext db, UserManager<AppUser> users, TokenService tokens, AuditService audit)
    {
        _db = db; _users = users; _tokens = tokens; _audit = audit;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostCreateAsync(string name)
    {
        var user = await _users.GetUserAsync(User);
        if (user is not null)
        {
            var (plain, _) = await _tokens.CreateAsync(user, name);
            NewToken = plain;
            await _audit.LogAsync(user.DisplayName, "token.created", name);
        }
        await LoadAsync();
    }

    public async Task OnPostRevokeAsync(int id)
    {
        var user = await _users.GetUserAsync(User);
        var token = await _db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == user!.Id);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(user!.DisplayName, "token.revoked", token.Name);
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var userId = _users.GetUserId(User);
        Tokens = await _db.ApiTokens.Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).ToListAsync();
    }
}

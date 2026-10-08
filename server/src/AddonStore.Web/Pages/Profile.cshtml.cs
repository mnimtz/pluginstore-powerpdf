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
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly SignInManager<AppUser> _signIn;
    private readonly CustomerService _customers;

    public List<ApiToken> Tokens { get; private set; } = new();
    public string? NewToken { get; private set; }
    public AppUser? Me { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    /// <summary>The personal test code (S1.6.0), null when none is active.</summary>
    public string? TestCode { get; private set; }

    public ProfileModel(AppDbContext db, UserManager<AppUser> users, TokenService tokens,
        AuditService audit, IConfiguration config, IWebHostEnvironment env, SignInManager<AppUser> signIn, CustomerService customers)
    {
        _db = db; _users = users; _tokens = tokens; _audit = audit; _config = config; _env = env; _signIn = signIn; _customers = customers;
    }

    // Personal test code (S1.6.0): shows the own versions that wait for review in the store window.
    public async Task OnPostTestCodeAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is not null) { await _customers.NewTestCodeAsync(user); Notice = "Test code created. Enter it in the store window with \"Customer code\"."; }
        await LoadAsync();
    }

    public async Task OnPostTestCodeRevokeAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is not null && await _customers.RevokeTestCodeAsync(user)) Notice = "Test code deleted; it stops working at once.";
        await LoadAsync();
    }

    // Change the own password (S1.0.6): other sessions are signed out, this one stays.
    public async Task OnPostPasswordAsync(string? current, string? password, string? password2)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) { await LoadAsync(); return; }
        NoticeKind = "error";
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(password) || password.Length > 200)
            Notice = "Enter the current and the new password (up to 200 characters).";
        else if (password != password2)
            Notice = "The two passwords do not match.";
        else
        {
            var result = await _users.ChangePasswordAsync(user, current, password);
            if (!result.Succeeded)
                Notice = string.Join(" ", result.Errors.Select(e => e.Description));
            else
            {
                await _signIn.RefreshSignInAsync(user);
                var revoked = await TokenService.RevokeAllAsync(_db, user.Id);   // audit S1.3.1
                await _audit.LogAsync(user.DisplayName, "user.password-changed", user.Email ?? user.Id, revoked > 0 ? $"{revoked} API tokens revoked" : "");
                Notice = revoked > 0 ? "Password changed. Other sessions were signed out and your API tokens revoked; create new ones if needed."
                                     : "Password changed. Other sessions were signed out.";
                NoticeKind = "ok";
            }
        }
        await LoadAsync();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostProfileAsync(string name, IFormFile? avatar, bool showContact, bool notifyPlugins)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) { await LoadAsync(); return; }

        if (user.NotifyAboutPlugins != notifyPlugins)
        {
            user.NotifyAboutPlugins = notifyPlugins;
            Notice = "Profile updated.";
        }

        if (user.ShowContactPublicly != showContact)
        {
            user.ShowContactPublicly = showContact;
            Notice = "Profile updated.";
        }

        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != user.DisplayName)
        {
            // names identify people in audit entries, notes and assignments: bounded and unique (audit S1.3.1)
            var n = name.Trim();
            if (n.Length > 100 || n.Any(char.IsControl))
            {
                Notice = "The name may have at most 100 characters."; NoticeKind = "error"; await LoadAsync(); return;
            }
            if (await DisplayNames.TakenAsync(_db.Users, n, user.Id))
            {
                Notice = "Another account already uses this name."; NoticeKind = "error"; await LoadAsync(); return;
            }
            user.DisplayName = n;
            Notice = "Profile updated.";
        }

        if (avatar is not null && avatar.Length > 0)
        {
            if (avatar.Length > 512 * 1024)
            {
                Notice = "The image is too large (maximum 512 KB).";
                NoticeKind = "error";
                await LoadAsync();
                return;
            }
            using var ms = new MemoryStream();
            await avatar.CopyToAsync(ms);
            var b = ms.ToArray();
            string? ext = null;
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') ext = "png";
            else if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ext = "jpg";
            if (ext is null)
            {
                Notice = "Only PNG or JPEG images are accepted.";
                NoticeKind = "error";
                await LoadAsync();
                return;
            }
            var dataRoot = _config["Storage:Data"];
            if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(_env.ContentRootPath, "data");
            var dir = Path.Combine(dataRoot, "avatars");
            Directory.CreateDirectory(dir);
            foreach (var old in new[] { "png", "jpg" })
            {
                var oldPath = Path.Combine(dir, $"{user.Id}.{old}");
                if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
            }
            await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, $"{user.Id}.{ext}"), b);
            user.AvatarFile = $"{user.Id}.{ext}";
            Notice = "Profile updated.";
        }

        await _users.UpdateAsync(user);
        await _audit.LogAsync(user.DisplayName, "profile.updated", user.Email ?? user.Id);
        await LoadAsync();
    }

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
        if (user is null) { await LoadAsync(); return; }
        var token = await _db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == user.Id);
        if (token is not null && token.RevokedAt is null)
        {
            token.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(user.DisplayName, "token.revoked", token.Name);
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Me = await _users.GetUserAsync(User);
        var userId = _users.GetUserId(User);
        Tokens = await _db.ApiTokens.Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).ToListAsync();
        TestCode = userId is null ? null : await _customers.TestCodeOfAsync(userId);
    }
}

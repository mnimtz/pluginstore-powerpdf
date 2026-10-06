using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

/// <summary>Admins review categories created by uploads: merge, delete, set the limit.</summary>
public class CategoriesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly CategoryService _categories;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;

    public record Row(Category Cat, string Name, int Packages);
    public List<Row> Rows { get; private set; } = new();
    public int Max { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public CategoriesModel(AppDbContext db, UserManager<AppUser> users, CategoryService categories,
        SettingsService settings, AuditService audit)
    {
        _db = db; _users = users; _categories = categories; _settings = settings; _audit = audit;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostMergeAsync(string slug, string into)
    {
        var admin = await _users.GetUserAsync(User);
        Notice = await _categories.MergeAsync(slug, into, admin!);
        if (Notice is null) { Notice = "This action is not possible for this category."; NoticeKind = "error"; }
        await LoadAsync();
    }

    public async Task OnPostDeleteAsync(string slug)
    {
        var admin = await _users.GetUserAsync(User);
        Notice = await _categories.DeleteAsync(slug, admin!);
        if (Notice is null) { Notice = "This action is not possible for this category."; NoticeKind = "error"; }
        await LoadAsync();
    }

    public async Task OnPostLimitAsync(int max)
    {
        var admin = await _users.GetUserAsync(User);
        if (max is >= 8 and <= 50)
        {
            await _settings.SetAsync("Categories.Max", max.ToString());
            await _audit.LogAsync(admin!.DisplayName, "settings.changed", "Categories.Max", max.ToString());
            Notice = "Settings saved.";
        }
        else { Notice = "The limit must be between 8 and 50."; NoticeKind = "error"; }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var lang = AddonStore.Web.Services.Lang.Current;
        var pkgs = await _db.Packages.Include(p => p.Versions).ToListAsync();
        var used = pkgs.GroupBy(CategoryService.EffectiveSlug).ToDictionary(g => g.Key, g => g.Count());
        Rows = (await _categories.AllAsync())
            .Select(c => new Row(c, CategoryService.Name(c, c.Slug, lang), used.GetValueOrDefault(c.Slug)))
            .OrderBy(r => r.Cat.Builtin ? 0 : 1).ThenBy(r => r.Name).ToList();
        Max = await _categories.MaxAsync();
    }
}

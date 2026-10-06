using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>Owner or admin edits the catalog entry (name, description, author, contact) of one package.</summary>
public class CatalogEntryModel : PageModel
{
    public static readonly (string Code, string Label)[] Languages =
    {
        ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("it", "Italiano"), ("es", "Español"),
        ("nl", "Nederlands"), ("pt", "Português"), ("da", "Dansk"), ("fi", "Suomi"), ("nb", "Norsk bokmål"),
        ("sv", "Svenska"), ("pl", "Polski"), ("cs", "Čeština"), ("hu", "Magyar"), ("ru", "Русский"), ("tr", "Türkçe"),
        ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"), ("ko", "한국어"), ("ar", "العربية"),
    };

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly PackageMetaService _meta;

    public Package? Pkg { get; private set; }
    public Dictionary<string, string> Name { get; private set; } = new();
    public Dictionary<string, string> Description { get; private set; } = new();
    public string Author { get; private set; } = "";
    public string Contact { get; private set; } = "";
    public string OwnerName { get; private set; } = "";
    public List<Category> Categories { get; private set; } = new();
    public string? CategoryOverride { get; private set; }
    public string ManifestCategory { get; private set; } = "";
    public List<MetaIssue> Issues { get; private set; } = new();
    public string? Notice { get; private set; }

    public CatalogEntryModel(AppDbContext db, UserManager<AppUser> users, PackageMetaService meta)
    {
        _db = db; _users = users; _meta = meta;
    }

    private async Task<(Package? pkg, AppUser? user)> LoadAsync(string id)
    {
        var user = await _users.GetUserAsync(User);
        var pkg = await _db.Packages.Include(p => p.Owner).Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (user is null || pkg is null) return (null, user);
        if (pkg.OwnerId != user.Id && !User.IsInRole("Admin")) return (null, user);
        Pkg = pkg;
        OwnerName = pkg.Owner?.DisplayName ?? "";
        return (pkg, user);
    }

    private void Fill()
    {
        var newest = Pkg!.Versions.Where(v => v.Status != VersionStatus.Withdrawn && v.Status != VersionStatus.Rejected)
            .OrderByDescending(v => v.Version, new SemVerComparer()).FirstOrDefault()
            ?? Pkg.Versions.OrderByDescending(v => v.SubmittedAt).FirstOrDefault();
        (Name, Description, Author, Contact) = PackageMetaService.Current(Pkg, newest);
        Categories = _db.Categories.OrderBy(c => c.Slug).ToList();
        CategoryOverride = Pkg.CategoryOverride;
        var tmp = new Package { Versions = Pkg.Versions };
        ManifestCategory = CategoryService.EffectiveSlug(tmp);
    }

    public async Task<IActionResult> OnGetAsync(string id)
    {
        var (pkg, _) = await LoadAsync(id ?? "");
        if (pkg is null) return Forbid();
        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string id, string? author, string? contact, string? category)
    {
        var (pkg, user) = await LoadAsync(id ?? "");
        if (pkg is null || user is null) return Forbid();

        Dictionary<string, string> Read(string prefix) => Languages
            .Select(l => (l.Code, Value: (string?)Request.Form[prefix + l.Code]))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Code, x => x.Value!);

        var change = new MetaChange
        {
            SetName = true, Name = Read("name_"),
            SetDescription = true, Description = Read("desc_"),
            SetAuthor = true, Author = author,
            SetContact = true, ContactEmail = contact,
            SetCategory = true, Category = category,
        };
        Issues = await _meta.ApplyAsync(pkg, user, change);
        if (Issues.Any(i => i.Severity == "error"))
        {
            Name = change.Name ?? new(); Description = change.Description ?? new();
            Author = author ?? ""; Contact = contact ?? "";
            Categories = _db.Categories.OrderBy(c => c.Slug).ToList();
            CategoryOverride = category;
            ManifestCategory = CategoryService.EffectiveSlug(new Package { Versions = pkg.Versions });
            return Page();
        }
        Notice = "Catalog entry saved. The catalog and the Power PDF client show it immediately.";
        Fill();
        return Page();
    }

    public async Task<IActionResult> OnPostResetAsync(string id)
    {
        var (pkg, user) = await LoadAsync(id ?? "");
        if (pkg is null || user is null) return Forbid();
        await _meta.ApplyAsync(pkg, user, new MetaChange { SetName = true, SetDescription = true, SetAuthor = true, SetContact = true, SetCategory = true });
        Notice = "Catalog entry reset to the values from the package.";
        Fill();
        return Page();
    }
}

using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Issues;

/// <summary>
/// The problem report queue (S1.1.0): developers see the reports of their own
/// add-ons, admins all. Sections by status on the left, filters on top, paged.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IssueService _issues;

    public IndexModel(AppDbContext db, UserManager<AppUser> users, IssueService issues)
    {
        _db = db; _users = users; _issues = issues;
    }

    /// <summary>Sections of the left navigation: key, label (resx key).</summary>
    public static readonly (string Key, string Label)[] Views =
    {
        ("active", "Active"), ("open", "open"), ("in_progress", "In progress"), ("waiting", "Waiting"),
        ("done", "done"), ("declined", "Declined"), ("all", "All"),
    };

    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Pkg { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Who { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public record Row(Feedback F, string Name);
    public List<Row> Rows { get; } = new();
    public int Total { get; private set; }
    public Dictionary<string, int> Counts { get; } = new();
    public List<(string Id, string Name)> PackageOptions { get; } = new();
    public bool IsAdmin { get; private set; }
    public string Me { get; private set; } = "";

    public async Task OnGetAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return;
        IsAdmin = User.IsInRole("Admin");
        Me = user.DisplayName;
        if (!Views.Any(v => v.Key == View)) View = "active";
        if (Kind is not ("problem" or "comment")) Kind = null;
        if (Who is not ("me" or "none")) Who = null;

        var scope = _issues.Scope(user, IsAdmin).AsNoTracking();
        var byStatus = await scope.GroupBy(f => f.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        foreach (var s in IssueService.Statuses) Counts[s] = byStatus.Where(x => x.Key == s).Sum(x => x.N);
        Counts["active"] = Counts["open"] + Counts["in_progress"] + Counts["waiting"];
        Counts["all"] = byStatus.Sum(x => x.N);

        // add-ons that have reports in the scope, with their display names
        var culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var ids = await scope.Select(f => f.PackageId).Distinct().ToListAsync();
        var pkgs = await _db.Packages.AsNoTracking().Include(p => p.Versions).Where(p => ids.Contains(p.Id)).ToListAsync();
        var names = pkgs.ToDictionary(p => p.Id, p => CatalogUi.DisplayName(p, p.Versions.OrderByDescending(v => v.SubmittedAt).FirstOrDefault(), culture));
        PackageOptions.AddRange(names.Select(n => (n.Key, n.Value)).OrderBy(n => n.Value, StringComparer.CurrentCultureIgnoreCase));
        if (Pkg is not null && !names.ContainsKey(Pkg)) Pkg = null;

        var q = scope;
        q = View switch
        {
            "active" => q.Where(f => f.Status != "done" && f.Status != "declined"),
            "all" => q,
            _ => q.Where(f => f.Status == View),
        };
        if (Pkg is not null) q = q.Where(f => f.PackageId == Pkg);
        if (Kind is not null) q = q.Where(f => f.Kind == Kind);
        if (Who == "me") q = q.Where(f => f.AssignedTo == Me);
        else if (Who == "none") q = q.Where(f => f.AssignedTo == null);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var term = Q.Trim();
            q = int.TryParse(term.TrimStart('#'), out var num) ? q.Where(f => f.Id == num) : q.Where(f => f.Message.Contains(term));
        }
        Total = await q.CountAsync();
        var rows = await q.OrderByDescending(f => f.Id).Skip(Paging.Skip(Request, "issues", Total)).Take(Paging.Size(Request)).ToListAsync();
        Rows.AddRange(rows.Select(f => new Row(f, names.GetValueOrDefault(f.PackageId, f.PackageId))));
    }

    /// <summary>Link to a section that keeps the filters.</summary>
    public string ViewUrl(string view) => Url.Page("/Issues/Index", new
    {
        view = view == "active" ? null : view, pkg = Pkg, kind = Kind, who = Who, q = Q,
    }) ?? "";
}

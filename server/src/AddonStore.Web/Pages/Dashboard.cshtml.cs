using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

public class DashboardModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly SubmissionService _svc;

    public record Row(Package Pkg, string Name, string Owner, PackageVersion? Live, PackageVersion? Beta,
                      PackageVersion? Newest, int Downloads, DateTime LastActivity)
    {
        public bool Pending => Beta is not null && (Live is null || new SemVerComparer().Compare(Beta.Version, Live.Version) > 0);
        public bool InStore => Live is not null || Beta is not null;
    }

    public List<Package> Packages { get; private set; } = new();
    public List<Row> Rows { get; private set; } = new();
    public List<Row> Visible { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public List<Finding> Findings { get; private set; } = new();
    public string? Notice { get; private set; }
    public string UserId => _users.GetUserId(User) ?? "";
    public string NoticeKind { get; private set; } = "ok";

    public DashboardModel(AppDbContext db, UserManager<AppUser> users, SubmissionService svc)
    {
        _db = db; _users = users; _svc = svc;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostAsync(IFormFile? package)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null || package is null || package.Length == 0)
        {
            Notice = "No package received.";
            NoticeKind = "error";
            await LoadAsync();
            return;
        }

        var tmp = Path.GetTempFileName();
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await package.CopyToAsync(fs);

            var result = await _svc.SubmitAsync(tmp, user, "web");
            Findings = result.Report.Findings;
            if (result.ErrorCode == "CLIENT_ADMIN_ONLY")
            {
                Notice = "Only administrators can publish new versions of the Add-on Store client.";
                NoticeKind = "error";
            }
            else if (result.ErrorCode == "VERSION_EXISTS")
            {
                Notice = result.ErrorHint;
                NoticeKind = "warn";
            }
            else if (result.Version is null)
            {
                Notice = "The package did not pass the automatic checks; nothing was stored. Fix the errors below and upload again.";
                NoticeKind = "error";
            }
            else if (result.Version.Status == VersionStatus.Live)
            {
                Notice = "Add-on Store client released. Installed clients offer the update now.";
            }
            else
            {
                Notice = "Submitted. The version is in the beta channel now and awaits admin review.";
            }
        }
        finally
        {
            try { System.IO.File.Delete(tmp); } catch { }
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var userId = _users.GetUserId(User);
        // Admins see every package so they can maintain the whole store.
        var isAdmin = User.IsInRole("Admin");
        Packages = await _db.Packages
            .Where(p => isAdmin || p.OwnerId == userId)
            .Include(p => p.Versions)
            .Include(p => p.Owner)
            .OrderBy(p => p.Id)
            .ToListAsync();

        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var cmp = new SemVerComparer();
        Rows = Packages.Select(p =>
        {
            var live = p.Versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var beta = p.Versions.Where(v => v.Status == VersionStatus.Beta).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var newest = p.Versions.OrderByDescending(v => v.SubmittedAt).FirstOrDefault();
            var last = new[] { newest?.SubmittedAt, p.Versions.Max(v => v.ReviewedAt), p.MetaUpdatedAt }
                .Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(p.CreatedAt).Max();
            return new Row(p, CatalogUi.DisplayName(p, live ?? beta ?? newest, lang), p.Owner?.DisplayName ?? "",
                           live, beta, newest, p.Versions.Sum(v => v.Downloads), last);
        }).ToList();

        IEnumerable<Row> rows = Rows;
        rows = Filter switch
        {
            "pending" => rows.Where(r => r.Pending),
            "live" => rows.Where(r => r.Live is not null),
            "beta" => rows.Where(r => r.Beta is not null),            // has a version in the beta stage
            "private" => rows.Where(r => r.Pkg.Visibility == "private"),
            "offline" => rows.Where(r => !r.InStore),
            _ => rows
        };
        if (!string.IsNullOrWhiteSpace(Q))
            rows = rows.Where(r => r.Name.Contains(Q, StringComparison.OrdinalIgnoreCase)
                                || r.Pkg.Id.Contains(Q, StringComparison.OrdinalIgnoreCase)
                                || r.Owner.Contains(Q, StringComparison.OrdinalIgnoreCase));
        Visible = rows.OrderByDescending(r => r.Pending).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

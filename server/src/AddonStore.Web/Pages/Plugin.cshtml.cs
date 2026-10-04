using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>One plug-in: catalog data, all versions and every action an owner, reviewer or admin may take.</summary>
public class PluginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly VersionActionService _actions;
    private readonly SourceService _sources;
    private readonly AiService _ai;
    private readonly AiAssist _assist;
    private readonly PackageMetaService _meta;

    public Package? Pkg { get; private set; }
    public AppUser? Me { get; private set; }
    public List<PackageVersion> Versions { get; private set; } = new();
    public CatalogItem? InCatalog { get; private set; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool IsAdmin { get; private set; }
    public bool IsOwner { get; private set; }
    public bool CanReview { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public string SourcePolicy { get; private set; } = "required";
    public List<Finding> SourceFindings { get; private set; } = new();
    public int[] RatingDist { get; private set; } = new int[5];   // index 0 = 1 star
    public List<Feedback> Feedbacks { get; private set; } = new();
    public bool AiReviewOn { get; private set; }

    public PluginModel(AppDbContext db, UserManager<AppUser> users, VersionActionService actions, SourceService sources,
                       AiService ai, AiAssist assist, PackageMetaService meta)
    {
        _db = db; _users = users; _actions = actions; _sources = sources; _ai = ai; _assist = assist; _meta = meta;
    }

    private async Task<bool> LoadAsync(string id)
    {
        Me = await _users.GetUserAsync(User);
        Pkg = await _db.Packages.Include(p => p.Owner).Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (Me is null || Pkg is null) return false;
        IsAdmin = User.IsInRole("Admin");
        IsOwner = Pkg.OwnerId == Me.Id;
        CanReview = VersionActionService.CanReview(User);
        if (!IsOwner && !IsAdmin && !CanReview) return false;

        var cmp = new SemVerComparer();
        Versions = Pkg.Versions.OrderByDescending(v => v.Version, cmp).ThenByDescending(v => v.SubmittedAt).ToList();
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var shown = Versions.FirstOrDefault(v => v.Status == VersionStatus.Live)
                    ?? Versions.FirstOrDefault(v => v.Status == VersionStatus.Beta) ?? Versions.FirstOrDefault();
        Name = CatalogUi.DisplayName(Pkg, shown, lang);
        Description = CatalogUi.OverrideText(Pkg.DescriptionJson, lang)
                      ?? (shown is null ? "" : CatalogUi.ManifestText(shown, "description", lang));
        InCatalog = (await CatalogUi.GetAsync(_db, lang, includeBeta: true)).FirstOrDefault(c => c.Id == Pkg.Id);
        SourcePolicy = await _sources.PolicyAsync();
        RatingDist = new int[5];
        foreach (var g in await _db.Ratings.AsNoTracking().Where(r => r.PackageId == Pkg.Id).GroupBy(r => r.Stars)
                                       .Select(g => new { g.Key, N = g.Count() }).ToListAsync())
            if (g.Key is >= 1 and <= 5) RatingDist[g.Key - 1] = g.N;
        Feedbacks = (IsOwner || IsAdmin)
            ? await _db.Feedbacks.AsNoTracking().Where(f => f.PackageId == Pkg.Id).OrderByDescending(f => f.Id).Take(200).ToListAsync()
            : new();
        var ai = await _ai.ConfigAsync();
        AiReviewOn = CanReview && ai.On && ai.Review;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string id) => await LoadAsync(id ?? "") ? Page() : Forbid();

    private async Task<IActionResult> ActAsync(string id, Func<Task<string?>> action)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        Notice = await action() ?? "This action is not allowed for this version.";
        if (Notice.StartsWith("The source code") || Notice.StartsWith("This action")) NoticeKind = "warn";
        await LoadAsync(id!);
        return Page();
    }

    public Task<IActionResult> OnPostApproveAsync(string id, int versionId) =>
        ActAsync(id, () => CanReview ? _actions.DecideAsync(versionId, Me!, true, null) : Task.FromResult<string?>(null));

    public Task<IActionResult> OnPostRejectAsync(string id, int versionId, string comment) =>
        ActAsync(id, () => CanReview ? _actions.DecideAsync(versionId, Me!, false, comment) : Task.FromResult<string?>(null));

    public Task<IActionResult> OnPostWithdrawAsync(string id, int versionId) =>
        ActAsync(id, () => _actions.WithdrawAsync(versionId, Me!, IsAdmin));

    public Task<IActionResult> OnPostRestoreAsync(string id, int versionId) =>
        ActAsync(id, () => _actions.RestoreAsync(versionId, Me!, IsAdmin));

    public async Task<IActionResult> OnPostSourceAsync(string id, int versionId, IFormFile? source)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        var v = Versions.FirstOrDefault(x => x.Id == versionId);
        if (v is null || (!IsOwner && !IsAdmin) || source is null || source.Length == 0)
        {
            Notice = "This action is not allowed for this version."; NoticeKind = "warn";
            return Page();
        }
        var tmp = Path.Combine(Path.GetTempPath(), "src-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var fs = System.IO.File.Create(tmp)) await source.CopyToAsync(fs);
            var report = await _sources.UploadAsync(v, tmp, Me!, IsAdmin);
            SourceFindings = report.Findings.ToList();
            Notice = report.Passed ? "Source code stored." : "The source code was not stored.";
            NoticeKind = report.Passed ? "ok" : "error";
        }
        finally { try { System.IO.File.Delete(tmp); } catch { } }
        await LoadAsync(id!);
        return Page();
    }

    public async Task<IActionResult> OnPostFeedbackStatusAsync(string id, int fid, string status)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        if (!IsOwner && !IsAdmin) return Forbid();
        var f = await _db.Feedbacks.FirstOrDefaultAsync(x => x.Id == fid && x.PackageId == id);
        if (f is not null && status is "open" or "done")
        {
            f.Status = status;
            f.DoneAt = status == "done" ? DateTime.UtcNow : null;
            f.DoneBy = status == "done" ? Me!.DisplayName : null;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    /// <summary>public (catalog) or private (only customers with a delivery and code see it).</summary>
    public async Task<IActionResult> OnPostVisibilityAsync(string id, string visibility)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        if (!IsOwner && !IsAdmin) return Forbid();
        var pkg = await _db.Packages.FirstAsync(p => p.Id == id);
        var issues = await _meta.ApplyAsync(pkg, Me!, new MetaChange { SetVisibility = true, Visibility = visibility });
        Notice = issues.Any(i => i.Severity == "error") ? "This action is not allowed for this version." : "Settings saved.";
        NoticeKind = issues.Any(i => i.Severity == "error") ? "warn" : "ok";
        await LoadAsync(id!);
        return Page();
    }

    public async Task<IActionResult> OnPostAiReviewAsync(string id, int versionId)
    {
        if (!await LoadAsync(id ?? "")) return Forbid();
        var v = Versions.FirstOrDefault(x => x.Id == versionId);
        if (v is null || !AiReviewOn)
        {
            Notice = "This action is not allowed for this version."; NoticeKind = "warn";
            return Page();
        }
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var ok = await _assist.ReviewAsync(v, lang, HttpContext.RequestAborted);
        Notice = ok ? "AI review aid created." : "The AI provider gave no usable answer. Check the connection test in the settings.";
        NoticeKind = ok ? "ok" : "error";
        await LoadAsync(id!);
        return Page();
    }

    public Task<IActionResult> OnPostWithdrawAllAsync(string id) =>
        ActAsync(id, () => _actions.WithdrawPackageAsync(id, Me!, IsAdmin));
}

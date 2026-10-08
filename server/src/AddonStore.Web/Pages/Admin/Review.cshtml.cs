using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

public class ReviewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly VersionActionService _actions;
    private readonly AiService _ai;
    private readonly AiAssist _assist;

    public List<PackageVersion> Queue { get; private set; } = new();
    /// <summary>Approved for beta, newer than the live version: candidates for the live store (S1.6.0).</summary>
    public List<PackageVersion> InBeta { get; private set; } = new();
    public Dictionary<string, Package> Packages { get; private set; } = new();
    public string? Notice { get; private set; }
    public bool AiReviewOn { get; private set; }

    public ReviewModel(AppDbContext db, UserManager<AppUser> users, VersionActionService actions, AiService ai, AiAssist assist)
    {
        _db = db; _users = users; _actions = actions; _ai = ai; _assist = assist;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostApproveAsync(int id, string[]? confirmed, string? stage)
    {
        Notice = await _actions.DecideAsync(id, (await _users.GetUserAsync(User))!, approve: true, comment: null, confirmed, stage ?? "live");
        await LoadAsync();
    }

    public async Task OnPostRejectAsync(int id, string comment)
    {
        Notice = await _actions.DecideAsync(id, (await _users.GetUserAsync(User))!, approve: false, comment);
        await LoadAsync();
    }

    public async Task OnPostWithdrawAsync(int id)
    {
        Notice = await _actions.WithdrawAsync(id, (await _users.GetUserAsync(User))!, User.IsInRole("Admin"));
        await LoadAsync();
    }

    public async Task OnPostAiReviewAsync(int versionId)
    {
        await LoadAsync();
        var v = Queue.FirstOrDefault(x => x.Id == versionId);
        if (v is null || !AiReviewOn) { Notice = "This action is not allowed for this version."; return; }
        var lang = (await HttpContext.RequestServices.GetRequiredService<AddonStore.Web.Services.AiService>().ConfigAsync()).ReviewLanguage;
        Notice = await _assist.ReviewAsync(v, lang, HttpContext.RequestAborted)
            ? "AI review aid created."
            : "The AI provider gave no usable answer. Check the connection test in the settings.";
        await LoadAsync();
    }

    /// <summary>Shows the review aid in another language (S1.11.0).</summary>
    public async Task OnPostAiTranslateAsync(int versionId, string? lang)
    {
        await LoadAsync();
        var v = Queue.FirstOrDefault(x => x.Id == versionId);
        if (v is null || !AiReviewOn) { Notice = "This action is not allowed for this version."; return; }
        var code = AddonStore.Web.Services.AiAssist.ReviewLanguage(lang);
        if (await _assist.TranslateReviewAsync(v, code, HttpContext.RequestAborted)) ViewData["AiLang:" + versionId] = code;
        else Notice = "The AI provider gave no usable answer. Check the connection test in the settings.";
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var ai = await _ai.ConfigAsync();
        AiReviewOn = ai.On && ai.Review;
        _db.ChangeTracker.Clear();
        // private add-ons are reviewed too (S1.11.0): no customer gets a version before its approval
        Queue = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Submitted && v.PackageId != SubmissionService.ClientPackageId)
            .OrderBy(v => v.SubmittedAt).ToListAsync();
        var cmp = new AddonStore.Web.Validation.SemVerComparer();
        var betas = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Beta &&
                                                         !_db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private")).ToListAsync();
        var lives = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Live).Select(v => new { v.PackageId, v.Version }).ToListAsync();
        InBeta = betas.GroupBy(v => v.PackageId)
            .Select(g => g.OrderByDescending(v => v.Version, cmp).First())
            .Where(b => !lives.Any(l => l.PackageId == b.PackageId && cmp.Compare(l.Version, b.Version) >= 0))
            .OrderBy(v => v.PackageId).ToList();
        var ids = Queue.Concat(InBeta).Select(q => q.PackageId).Distinct().ToList();
        Packages = await _db.Packages.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
    }
}

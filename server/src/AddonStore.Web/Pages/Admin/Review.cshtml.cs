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
    public Dictionary<string, Package> Packages { get; private set; } = new();
    public string? Notice { get; private set; }
    public bool AiReviewOn { get; private set; }

    public ReviewModel(AppDbContext db, UserManager<AppUser> users, VersionActionService actions, AiService ai, AiAssist assist)
    {
        _db = db; _users = users; _actions = actions; _ai = ai; _assist = assist;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostApproveAsync(int id, string[]? confirmed)
    {
        Notice = await _actions.DecideAsync(id, (await _users.GetUserAsync(User))!, approve: true, comment: null, confirmed);
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
        var lang = AddonStore.Web.Services.Lang.Current;
        Notice = await _assist.ReviewAsync(v, lang, HttpContext.RequestAborted)
            ? "AI review aid created."
            : "The AI provider gave no usable answer. Check the connection test in the settings.";
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var ai = await _ai.ConfigAsync();
        AiReviewOn = ai.On && ai.Review;
        _db.ChangeTracker.Clear();
        // private add-ons need no approval: they reach customers through deliveries
        Queue = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Beta &&
                                                     !_db.Packages.Any(p => p.Id == v.PackageId && p.Visibility == "private"))
            .OrderBy(v => v.SubmittedAt).ToListAsync();
        Packages = await _db.Packages.Where(p => Queue.Select(q => q.PackageId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
    }
}

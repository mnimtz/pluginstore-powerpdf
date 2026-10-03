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

    public List<PackageVersion> Queue { get; private set; } = new();
    public Dictionary<string, Package> Packages { get; private set; } = new();
    public string? Notice { get; private set; }

    public ReviewModel(AppDbContext db, UserManager<AppUser> users, VersionActionService actions)
    {
        _db = db; _users = users; _actions = actions;
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task OnPostApproveAsync(int id)
    {
        Notice = await _actions.DecideAsync(id, (await _users.GetUserAsync(User))!, approve: true, comment: null);
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

    private async Task LoadAsync()
    {
        Queue = await _db.PackageVersions.Where(v => v.Status == VersionStatus.Beta)
            .OrderBy(v => v.SubmittedAt).ToListAsync();
        Packages = await _db.Packages.Where(p => Queue.Select(q => q.PackageId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
    }
}

using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages;

/// <summary>Developer dashboard (S1.1.0): own add-ons for developers, all for admins (optionally one developer).</summary>
public class InsightsModel : PageModel
{
    private readonly InsightsService _insights;
    private readonly UserManager<AppUser> _users;

    public InsightsModel(InsightsService insights, UserManager<AppUser> users) { _insights = insights; _users = users; }

    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
    [BindProperty(SupportsGet = true)] public string? Pkg { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dev { get; set; }
    public InsightsService.Result? R { get; private set; }
    public bool IsAdmin { get; private set; }

    public async Task OnGetAsync()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return;
        IsAdmin = User.IsInRole("Admin");
        R = await _insights.ComputeAsync(user, IsAdmin, Days, Pkg, Dev,
            AddonStore.Web.Services.Lang.Current);
        Days = R.Days; Pkg = R.Package; Dev = R.OwnerId;
    }

    public string Link(int? days = null, string? pkg = "\0", string? dev = "\0") => Url.Page("/Insights", new
    {
        days = (days ?? Days) == 30 ? (int?)null : days ?? Days,
        pkg = pkg == "\0" ? Pkg : pkg,
        dev = dev == "\0" ? Dev : dev,
    }) ?? "";
}

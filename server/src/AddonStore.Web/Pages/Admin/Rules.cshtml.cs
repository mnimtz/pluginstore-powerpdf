using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// Every rule of the store, grouped by area (S1.0.9). Signed-in users read it;
/// admins extend it with house rules (guidelines reviewers check) and make
/// warnings mandatory (the validator then treats them as errors).
/// </summary>
public class RulesModel : PageModel
{
    public const string StoreView = "store";
    public static readonly string[] Kinds = { "review", "recommendation" };
    public const int MaxTitle = 120, MaxText = 2000;

    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly UserManager<AppUser> _users;

    [BindProperty(SupportsGet = true)] public string? Area { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sev { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Edit { get; set; }

    public bool IsAdmin => User.IsInRole("Admin");
    public RuleCatalog.HouseState House { get; private set; } = RuleCatalog.HouseState.Empty;
    public List<RuleCatalog.Rule> Shown { get; private set; } = new();
    public RuleCatalog.HouseRule? Editing { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public RulesModel(SettingsService settings, AuditService audit, UserManager<AppUser> users)
    {
        _settings = settings; _audit = audit; _users = users;
    }

    public static string SeverityOf(RuleCatalog.Rule r, RuleCatalog.HouseState h) =>
        r.Api ? "api" : r.Severity == "warning" && h.Escalated.Contains(r.Code) ? "error" : r.Severity;

    public int Count(string area) => RuleCatalog.Rules.Count(r => r.Area == area) + House.Rules.Count(h => h.Area == area);

    public void OnGet(string? saved)
    {
        if (saved == "1") Notice = "Saved.";
        Load();
    }

    private void Load()
    {
        House = RuleCatalog.House;
        if (Area != StoreView && !RuleCatalog.Areas.Contains(Area)) Area = string.IsNullOrWhiteSpace(Q) ? RuleCatalog.Areas[0] : null;
        IEnumerable<RuleCatalog.Rule> rules = RuleCatalog.Rules;
        var q = (Q ?? "").Trim();
        if (q.Length > 0)
            rules = rules.Where(r => r.Code.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase));
        else if (Area is not null && Area != StoreView)
            rules = rules.Where(r => r.Area == Area);
        else
            rules = Array.Empty<RuleCatalog.Rule>();
        if (Sev is "error" or "warning" or "info" or "api")
            rules = rules.Where(r => SeverityOf(r, House) == Sev);
        Shown = rules.ToList();
        Editing = Edit is null ? null : House.Rules.FirstOrDefault(h => h.Id == Edit);
    }

    private IActionResult Back(string? area) => RedirectToPage("/Admin/Rules", new { area, saved = "1" });

    public async Task<IActionResult> OnPostEscalateAsync(string code, bool on, string? area)
    {
        if (!IsAdmin) return Forbid();
        var rule = RuleCatalog.Rules.FirstOrDefault(r => r.Code == code && r.Severity == "warning" && !r.Api);
        if (rule is null) { Notice = "Only warnings can be made mandatory."; NoticeKind = "error"; Load(); return Page(); }
        var esc = RuleCatalog.House.Escalated.ToHashSet();
        if (on) esc.Add(code); else esc.Remove(code);
        await RuleCatalog.SaveAsync(_settings, RuleCatalog.House with { Escalated = esc.OrderBy(c => c).ToList() });
        var me = await _users.GetUserAsync(User);
        await _audit.LogAsync(me?.DisplayName ?? "", on ? "rules.escalated" : "rules.relaxed", code);
        return Back(area);
    }

    public async Task<IActionResult> OnPostHouseSaveAsync(string? id, string? area, string? title, string? text, string? kind)
    {
        if (!IsAdmin) return Forbid();
        title = (title ?? "").Trim(); text = (text ?? "").Trim();
        if (!RuleCatalog.Areas.Contains(area) || title.Length is 0 or > MaxTitle || text.Length is 0 or > MaxText || !Kinds.Contains(kind))
        {
            Notice = "Fill in area, title (up to 120 characters) and text (up to 2000 characters).";
            NoticeKind = "error";
            Area = area; Load();
            return Page();
        }
        var me = await _users.GetUserAsync(User);
        var list = RuleCatalog.House.Rules.ToList();
        var old = id is null ? null : list.FirstOrDefault(h => h.Id == id);
        var rule = new RuleCatalog.HouseRule(old?.Id ?? "H" + Guid.NewGuid().ToString("N")[..8], area!, title, text, kind!,
                                             old?.CreatedBy ?? me?.DisplayName ?? "", old?.CreatedAt ?? DateTime.UtcNow);
        if (old is null) list.Add(rule); else list[list.IndexOf(old)] = rule;
        await RuleCatalog.SaveAsync(_settings, RuleCatalog.House with { Rules = list });
        await _audit.LogAsync(me?.DisplayName ?? "", old is null ? "rules.house.added" : "rules.house.changed", rule.Id, $"{area}: {title}");
        return Back(area);
    }

    public async Task<IActionResult> OnPostHouseDeleteAsync(string id, string? area)
    {
        if (!IsAdmin) return Forbid();
        var list = RuleCatalog.House.Rules.Where(h => h.Id != id).ToList();
        if (list.Count == RuleCatalog.House.Rules.Count) return Back(area);
        await RuleCatalog.SaveAsync(_settings, RuleCatalog.House with { Rules = list });
        var me = await _users.GetUserAsync(User);
        await _audit.LogAsync(me?.DisplayName ?? "", "rules.house.deleted", id);
        return Back(area);
    }
}

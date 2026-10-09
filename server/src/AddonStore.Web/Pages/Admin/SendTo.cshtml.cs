using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// "Senden an" for store admins: overview, reports, blocks, settings
/// (incl. document storage) and the log. Hidden while the master switch is off
/// (the switch itself lives under Settings → Features).
/// </summary>
public class SendToModel : PageModel
{
    public static readonly (string Key, string Label)[] Views =
    {
        ("overview", "Overview"), ("reports", "Contact reports"), ("blocks", "Blocks"), ("settings", "Settings"), ("log", "Log"),
    };

    private readonly AppDbContext _db;
    private readonly SendToService _svc;
    private readonly SendToStorage _storage;
    private readonly SettingsService _settings;
    private readonly AuditService _audit;
    private readonly UserManager<AppUser> _users;

    public SendToModel(AppDbContext db, SendToService svc, SendToStorage storage, SettingsService settings, AuditService audit,
                       UserManager<AppUser> users)
    {
        _db = db; _svc = svc; _storage = storage; _settings = settings; _audit = audit; _users = users;
    }

    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";

    public SendToConfig Config { get; private set; } = null!;
    public dynamic? Overview { get; private set; }
    public string StorageKind { get; private set; } = "none";
    public string Container { get; private set; } = "sendto";
    public List<(string level, string text)> TestFindings { get; private set; } = new();

    public record ReportRow(SendToReport Report, string Reporter, string Reported, bool ReportedBlocked, string ReportedId);
    public List<ReportRow> Reports { get; private set; } = new();
    public List<SendToUser> Users { get; private set; } = new();
    public int UserTotal { get; private set; }
    public List<AuditEntry> Log { get; private set; } = new();
    public int LogTotal { get; private set; }

    public string CurrentView => Views.Any(v => v.Key == View) ? View! : "overview";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!(await _svc.ConfigAsync()).Enabled) return Redirect("/Admin/Settings?view=features");
        await LoadAsync();
        return Page();
    }

    private async Task<string> AdminNameAsync() => (await _users.GetUserAsync(User))?.DisplayName ?? "admin";

    public async Task<IActionResult> OnPostSettingsAsync(int retentionValue, string retentionUnit, int maxFileMB, int maxPendingMB,
                                                         bool quickSend, int inviteExpiryDays, int maxOpenInvites, int maxInvitesPerDay,
                                                         int undoSeconds)
    {
        var hours = retentionUnit == "days" ? retentionValue * 24 : retentionValue;
        if (hours is < 1 or > SendToService.MaxRetentionHours)
        {
            Notice = "The retention time must be between 1 hour and 30 days."; NoticeKind = "error";
        }
        else
        {
            var values = new Dictionary<string, string>
            {
                ["RetentionHours"] = hours.ToString(), ["MaxFileMB"] = Math.Clamp(maxFileMB, 1, 1024).ToString(),
                ["MaxPendingMB"] = Math.Clamp(maxPendingMB, 1, 10240).ToString(), ["QuickSend"] = quickSend ? "true" : "false",
                ["InviteExpiryDays"] = Math.Clamp(inviteExpiryDays, 1, 60).ToString(), ["MaxOpenInvites"] = Math.Clamp(maxOpenInvites, 1, 200).ToString(),
                ["MaxInvitesPerDay"] = Math.Clamp(maxInvitesPerDay, 1, 500).ToString(), ["UndoSeconds"] = Math.Clamp(undoSeconds, 0, 60).ToString(),
            };
            var admin = await AdminNameAsync();
            foreach (var (k, v) in values)
            {
                await _settings.SetAsync("SendTo." + k, v);
                await _audit.LogAsync(admin, "settings.changed", "SendTo." + k, v);
            }
            Notice = "Settings saved.";
        }
        View = "settings";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostStorageAsync(string? access, string? container, bool clear)
    {
        var admin = await AdminNameAsync();
        if (clear)
        {
            await _storage.ClearAccessAsync();
            await _audit.LogAsync(admin, "settings.changed", SendToStorage.AccessKey, "cleared");
            Notice = "Settings saved.";
        }
        else if (!string.IsNullOrWhiteSpace(access) || !string.IsNullOrWhiteSpace(container))
        {
            var c = (container ?? "").Trim().ToLowerInvariant();
            if (c.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$"))
            {
                Notice = "Container names are 3 to 63 lowercase letters, digits and hyphens."; NoticeKind = "error";
            }
            else
            {
                await _storage.SaveAccessAsync(access ?? "", c);
                await _audit.LogAsync(admin, "settings.changed", SendToStorage.AccessKey, "updated (secret not logged)");
                (var ok, TestFindings) = await _storage.TestAsync();
                Notice = ok ? "Settings saved." : "Saved, but the connection test found problems.";
                NoticeKind = ok ? "ok" : "error";
            }
        }
        View = "settings";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        (var ok, TestFindings) = await _storage.TestAsync();
        Notice = ok ? "Connection test passed." : "The connection test found problems.";
        NoticeKind = ok ? "ok" : "error";
        View = "settings";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostReportDoneAsync(int id)
    {
        var r = await _db.SendToReports.FirstOrDefaultAsync(x => x.Id == id);
        if (r is not null && r.Status == "open")
        {
            r.Status = "done"; r.DoneAt = DateTime.UtcNow; r.DoneBy = await AdminNameAsync();
            await _db.SaveChangesAsync();
            Notice = "Saved.";
        }
        View = "reports";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostBlockAsync(string userId, bool block, string? returnView)
    {
        try
        {
            await _svc.SetUserBlockedAsync(userId, block, await AdminNameAsync());
            Notice = block ? "User blocked." : "User unblocked.";
        }
        catch (SendToError) { Notice = "User not found."; NoticeKind = "error"; }
        View = returnView is "reports" ? "reports" : "blocks";
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Config = await _svc.ConfigAsync();
        StorageKind = await _storage.KindAsync();
        Container = await _storage.ContainerNameAsync();
        switch (CurrentView)
        {
            case "overview":
                Overview = await _svc.OverviewAsync();
                break;
            case "reports":
            {
                var rows = await _db.SendToReports.OrderBy(r => r.Status == "open" ? 0 : 1).ThenByDescending(r => r.CreatedAt).Take(200).ToListAsync();
                var ids = rows.SelectMany(r => new[] { r.ReporterId, r.ReportedId }).Distinct().ToList();
                var users = await _db.SendToUsers.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
                Reports = rows.Select(r => new ReportRow(r,
                    users.TryGetValue(r.ReporterId, out var a) ? $"{a.Name} ({a.Email})" : "?",
                    users.TryGetValue(r.ReportedId, out var b) ? $"{b.Name} ({b.Email})" : "?",
                    users.TryGetValue(r.ReportedId, out var c) && c.BlockedAt is not null, r.ReportedId)).ToList();
                break;
            }
            case "blocks":
            {
                var q = _db.SendToUsers.AsQueryable();
                if (!string.IsNullOrWhiteSpace(Q))
                {
                    var t = Q.Trim().ToLowerInvariant();
                    q = q.Where(u => u.Email.Contains(t) || u.Name.ToLower().Contains(t));
                }
                else q = q.Where(u => u.BlockedAt != null);
                UserTotal = await q.CountAsync();
                Users = await q.OrderByDescending(u => u.BlockedAt).ThenBy(u => u.Email)
                    .Skip(Paging.Skip(Request, "users", UserTotal)).Take(Paging.Size(Request)).ToListAsync();
                break;
            }
            case "log":
            {
                var q = _db.AuditEntries.Where(a => a.Action.StartsWith("sendto.") || a.Subject.StartsWith("SendTo."));
                LogTotal = await q.CountAsync();
                Log = await q.OrderByDescending(a => a.At).Skip(Paging.Skip(Request, "log", LogTotal)).Take(Paging.Size(Request)).ToListAsync();
                break;
            }
        }
    }
}

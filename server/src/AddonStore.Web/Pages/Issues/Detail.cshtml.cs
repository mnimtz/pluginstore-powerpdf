using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Issues;

/// <summary>One problem report (S1.1.0): message, log, attachments, AI assessment, notes; status, assignment, reply.</summary>
public class DetailModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IssueService _issues;

    public DetailModel(AppDbContext db, UserManager<AppUser> users, IssueService issues)
    {
        _db = db; _users = users; _issues = issues;
    }

    public Feedback? F { get; private set; }
    public string Name { get; private set; } = "";
    public List<FeedbackNote> Notes { get; private set; } = new();
    public List<FeedbackAttachment> Files { get; private set; } = new();
    public List<string> Assignees { get; private set; } = new();
    public int RetentionDays { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    public string? Draft { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostStatusAsync(int id, string status)
    {
        var (user, f) = await TargetAsync(id);
        if (f is null) return NotFound();
        var err = await _issues.SetStatusAsync(f, status, user!, viaApi: false);
        Done(err, "Status changed.");
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(int id, string? assignee)
    {
        var (user, f) = await TargetAsync(id);
        if (f is null) return NotFound();
        var err = await _issues.AssignAsync(f, assignee, user!, viaApi: false);
        Done(err, "Assignment changed.");
        await LoadAsync(id);
        return Page();
    }

    public async Task<IActionResult> OnPostNoteAsync(int id, string? text, string? mode)
    {
        var (user, f) = await TargetAsync(id);
        if (f is null) return NotFound();
        var reply = mode == "reply";
        var (_, err) = await _issues.AddNoteAsync(f, text, reply, user!, viaApi: false);
        Done(err, reply ? "Reply sent to the reporter." : "Note saved.");
        if (err is not null) Draft = text;
        await LoadAsync(id);
        return Page();
    }

    private void Done(IssueService.Problem? err, string ok)
    {
        if (err is null) { Notice = ok; NoticeKind = "ok"; }
        else { Notice = err.Code switch
            {
                "NOTE_INVALID" => "Write 2 to 4000 characters.",
                "NO_REPLY_ADDRESS" => "The reporter left no email address; add an internal note instead.",
                "MAIL_FAILED" => "The reply could not be sent. Check the email settings.",
                "ASSIGNEE_INVALID" => "Only the owner of the add-on or an admin can be assigned.",
                _ => "This action is not possible.",
            };
            NoticeKind = "error"; }
    }

    private async Task<(AppUser? User, Feedback? F)> TargetAsync(int id)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return (null, null);
        return (user, await _issues.FindAsync(id, user, User.IsInRole("Admin")));
    }

    private async Task<bool> LoadAsync(int id)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null) return false;
        F = await _issues.Scope(user, User.IsInRole("Admin")).AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (F is null) return false;
        var pkg = await _db.Packages.AsNoTracking().Include(p => p.Versions).FirstOrDefaultAsync(p => p.Id == F.PackageId);
        var culture = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Name = pkg is null ? F.PackageId : CatalogUi.DisplayName(pkg, pkg.Versions.OrderByDescending(v => v.SubmittedAt).FirstOrDefault(), culture);
        Notes = await _db.FeedbackNotes.AsNoTracking().Where(n => n.FeedbackId == id).OrderBy(n => n.Id).ToListAsync();
        // metadata only; the bytes are served by /api/feedback/{id}/attachments/{aid}
        Files = await _db.FeedbackAttachments.AsNoTracking().Where(a => a.FeedbackId == id).OrderBy(a => a.Id)
            .Select(a => new FeedbackAttachment { Id = a.Id, FeedbackId = a.FeedbackId, FileName = a.FileName, ContentType = a.ContentType,
                                                  Size = a.Size, Sha256 = a.Sha256, CreatedAt = a.CreatedAt, PurgedAt = a.PurgedAt,
                                                  Data = a.Data == null ? null : Array.Empty<byte>() })
            .ToListAsync();
        Assignees = await _issues.AssigneesAsync(F.PackageId);
        RetentionDays = await _issues.RetentionDaysAsync();
        return true;
    }
}

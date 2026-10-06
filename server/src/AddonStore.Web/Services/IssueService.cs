using System.Security.Cryptography;
using System.Text;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Services;

/// <summary>
/// The queue of problem reports (S1.1.0): status, assignment, notes, replies to
/// the reporter, attachments and their retention. Owners see the reports of
/// their own add-ons, admins all of them; the same rules hold for the portal
/// and the API (an AI assistant with a token works through the same queue).
/// </summary>
public class IssueService
{
    public static readonly string[] Statuses = { "open", "in_progress", "waiting", "done", "declined" };
    public static readonly HashSet<string> Closed = new() { "done", "declined" };
    public const int MaxFiles = 3;
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const long MaxTotalBytes = 10L * 1024 * 1024;
    public const int MaxNote = 4000;
    public const int MaxRepliesPerDay = 50;
    public const int DefaultRetentionDays = 180;
    public const string RetentionKey = "Feedback.RetentionDays";

    private readonly AppDbContext _db;
    private readonly NotificationService _notify;
    private readonly AuditService _audit;
    private readonly SettingsService _settings;

    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    public IssueService(AppDbContext db, NotificationService notify, AuditService audit, SettingsService settings,
                        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _db = db; _notify = notify; _audit = audit; _settings = settings; _cache = cache;
    }

    public record AttachmentIn(string? Name, string? ContentType, string? Data);
    public record Problem(string Code, string Message);

    /// <summary>Reports the user may see: own add-ons, or all for admins.</summary>
    public IQueryable<Feedback> Scope(AppUser user, bool admin)
    {
        var q = _db.Feedbacks.AsQueryable();
        if (admin) return q;
        var mine = _db.Packages.Where(p => p.OwnerId == user.Id).Select(p => p.Id);
        return q.Where(f => mine.Contains(f.PackageId));
    }

    public async Task<Feedback?> FindAsync(int id, AppUser user, bool admin) =>
        await Scope(user, admin).FirstOrDefaultAsync(f => f.Id == id);

    public static string Label(string status) => status switch
    {
        "in_progress" => "In progress", "waiting" => "Waiting", "done" => "done", "declined" => "Declined", _ => "open",
    };

    public static string Badge(string status) => status switch
    {
        "in_progress" => "submitted", "waiting" => "beta", "done" => "live", "declined" => "withdrawn", _ => "pending",
    };

    // ---- attachments -----------------------------------------------------------

    /// <summary>
    /// Checks attachments sent with a report: at most 3 files, 5 MB each, 10 MB
    /// together; the type comes from the content, not the name. Allowed:
    /// screenshots (PNG, JPEG), PDF and plain text (log, txt).
    /// </summary>
    public static (List<FeedbackAttachment> Files, Problem? Error) ReadAttachments(IReadOnlyList<AttachmentIn>? items)
    {
        var list = new List<FeedbackAttachment>();
        if (items is null || items.Count == 0) return (list, null);
        if (items.Count > MaxFiles) return (list, new("ATTACHMENT_INVALID", $"At most {MaxFiles} attachments per report."));
        long total = 0;
        foreach (var a in items)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(a.Data ?? ""); }
            catch (FormatException) { return (list, new("ATTACHMENT_INVALID", "An attachment is not valid base64.")); }
            if (bytes.Length == 0) return (list, new("ATTACHMENT_INVALID", "An attachment is empty."));
            if (bytes.Length > MaxFileBytes) return (list, new("ATTACHMENT_TOO_LARGE", $"Each attachment may have at most {MaxFileBytes / 1024 / 1024} MB."));
            total += bytes.Length;
            if (total > MaxTotalBytes) return (list, new("ATTACHMENT_TOO_LARGE", $"All attachments together may have at most {MaxTotalBytes / 1024 / 1024} MB."));
            var type = Sniff(bytes);
            if (type is null) return (list, new("ATTACHMENT_INVALID", "Allowed attachments: PNG, JPEG, PDF and plain text (log, txt)."));
            list.Add(new FeedbackAttachment
            {
                FileName = SafeName(a.Name, type), ContentType = type, Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Data = bytes,
            });
        }
        return (list, null);
    }

    private static string? Sniff(byte[] b)
    {
        bool Starts(params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(x => x);
        if (Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
        if (Starts(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (Starts(0x25, 0x50, 0x44, 0x46, 0x2D)) return "application/pdf";
        // plain text: valid UTF-8 (BOM optional) without NUL and with few control characters
        if (Array.IndexOf(b, (byte)0) >= 0) return null;
        try
        {
            var s = new UTF8Encoding(false, true).GetString(b);
            var ctrl = s.Count(c => c < 0x20 && c is not ('\r' or '\n' or '\t' or '\f'));
            return ctrl <= s.Length / 100 ? "text/plain" : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static string SafeName(string? name, string type)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim(' ', '.');
        if (n.Length > 120) n = n[..120];
        var ext = type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "application/pdf" => ".pdf", _ => ".txt" };
        if (n.Length == 0) n = "attachment";
        var has = Path.GetExtension(n).ToLowerInvariant();
        var fits = type switch
        {
            "image/jpeg" => has is ".jpg" or ".jpeg",
            "text/plain" => has is ".txt" or ".log" or ".json" or ".xml" or ".csv" or ".ini",
            _ => has == ext,
        };
        return fits ? n : n + ext;
    }

    // ---- changes ---------------------------------------------------------------

    public async Task<Problem?> SetStatusAsync(Feedback f, string status, AppUser user, bool viaApi)
    {
        status = (status ?? "").Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) return new("FEEDBACK_STATUS_INVALID", "status must be one of: " + string.Join(", ", Statuses) + ".");
        if (f.Status == status) return null;
        var old = f.Status;
        f.Status = status;
        var wasClosed = Closed.Contains(old);
        f.DoneAt = !Closed.Contains(status) ? null : wasClosed ? f.DoneAt : DateTime.UtcNow;   // done <-> declined keeps the retention clock
        f.DoneBy = !Closed.Contains(status) ? null : wasClosed ? f.DoneBy : user.DisplayName;
        f.UpdatedAt = DateTime.UtcNow;
        _db.FeedbackNotes.Add(new FeedbackNote { FeedbackId = f.Id, Author = user.DisplayName, Kind = "status", Text = old + " -> " + status, ViaApi = viaApi });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.DisplayName, "feedback.status", $"{f.PackageId} #{f.Id}", $"{old} -> {status}{(viaApi ? " (API)" : "")}");
        return null;
    }

    /// <summary>Owner of the add-on and the admins may work on a report; null clears the assignment.</summary>
    public async Task<Problem?> AssignAsync(Feedback f, string? assignee, AppUser user, bool viaApi)
    {
        assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim();
        if (assignee is not null && !(await AssigneesAsync(f.PackageId)).Contains(assignee))
            return new("ASSIGNEE_INVALID", "assignee must be the owner of the add-on or an admin (display name), or empty.");
        if (f.AssignedTo == assignee) return null;
        f.AssignedTo = assignee;
        f.UpdatedAt = DateTime.UtcNow;
        _db.FeedbackNotes.Add(new FeedbackNote { FeedbackId = f.Id, Author = user.DisplayName, Kind = "status",
            Text = assignee is null ? "unassigned" : "assigned to " + assignee, ViaApi = viaApi });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.DisplayName, "feedback.assign", $"{f.PackageId} #{f.Id}", assignee ?? "-");
        return null;
    }

    public async Task<List<string>> AssigneesAsync(string packageId)
    {
        var names = new List<string>();
        var owner = await _db.Packages.Where(p => p.Id == packageId).Select(p => p.Owner!.DisplayName).FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(owner)) names.Add(owner);
        var adminRole = await _db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstOrDefaultAsync();
        var admins = await _db.UserRoles.Where(r => r.RoleId == adminRole).Join(_db.Users, r => r.UserId, u => u.Id, (r, u) => u)
            .Where(u => u.Status == UserStatus.Active).Select(u => u.DisplayName).ToListAsync();
        names.AddRange(admins.Where(a => !string.IsNullOrEmpty(a)));
        return names.Distinct().OrderBy(n => n).ToList();
    }

    /// <summary>
    /// Internal note, or reply mailed to the reporter (only when the report has an
    /// address and mail is configured). A reply moves an open report to "waiting".
    /// </summary>
    public async Task<(FeedbackNote? Note, Problem? Error)> AddNoteAsync(Feedback f, string? text, bool reply, AppUser user, bool viaApi)
    {
        text = (text ?? "").Trim();
        if (text.Length < 2 || text.Length > MaxNote) return (null, new("NOTE_INVALID", $"text must have 2 to {MaxNote} characters."));
        string? sentTo = null;
        if (reply)
        {
            if (string.IsNullOrEmpty(f.Email)) return (null, new("NO_REPLY_ADDRESS", "The reporter left no email address; add an internal note instead."));
            var dayStart = DateTime.UtcNow.Date;
            // per account, not per display name (a rename must not reset it, S1.4.3)
            var perUser = _cache?.GetOrCreate($"replies:{user.Id}:{dayStart:yyyyMMdd}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(25); return new int[1]; });
            if (await _db.FeedbackNotes.CountAsync(n => n.Kind == "reply" && n.Author == user.DisplayName && n.At >= dayStart) >= MaxRepliesPerDay ||
                (perUser is not null && perUser[0] >= MaxRepliesPerDay))
                return (null, new("RATE_LIMITED", $"At most {MaxRepliesPerDay} replies to reporters per day."));
            if (perUser is not null) lock (perUser) perUser[0]++;
            var pkg = await _db.Packages.AsNoTracking().Include(p => p.Versions).FirstOrDefaultAsync(p => p.Id == f.PackageId);
            var name = pkg is null ? f.PackageId
                : CatalogUi.DisplayName(pkg, pkg.Versions.OrderByDescending(v => v.SubmittedAt).FirstOrDefault(), "en");
            static string enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
            // the text is the developer's, not the store's: say so above it (audit S1.3.1)
            var html = $"<p style=\"color:#8094AA\">Reply from {enc(user.DisplayName)}, who looks after the add-on {enc(name)}, sent through the Add-on Store. " +
                       "The store forwards this message; it did not write it.</p>" +
                       $"<p>{enc(text).Replace("\n", "<br />")}</p>" +
                       $"<p style=\"color:#8094AA\">About your report on {enc(name)} {enc(f.Version)}:</p>" +
                       $"<blockquote style=\"white-space:pre-wrap;color:#8094AA\">{enc(f.Message.Length > 1500 ? f.Message[..1500] + " ..." : f.Message)}</blockquote>";
            var sent = await _notify.SendDirectAsync(f.Email, $"[Add-on Store] Re: your report on {name}", html, "Feedback");
            if (!sent.Sent) return (null, new("MAIL_FAILED", "The reply could not be sent: " + sent.Detail));
            sentTo = f.Email;
        }
        var note = new FeedbackNote { FeedbackId = f.Id, Author = user.DisplayName, Kind = reply ? "reply" : "note", Text = text, SentTo = sentTo, ViaApi = viaApi };
        _db.FeedbackNotes.Add(note);
        f.UpdatedAt = DateTime.UtcNow;
        if (reply && f.Status == "open")
        {
            f.Status = "waiting";
            _db.FeedbackNotes.Add(new FeedbackNote { FeedbackId = f.Id, Author = user.DisplayName, Kind = "status", Text = "open -> waiting", ViaApi = viaApi });
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync(user.DisplayName, reply ? "feedback.reply" : "feedback.note", $"{f.PackageId} #{f.Id}", viaApi ? "API" : "");
        return (note, null);
    }

    // ---- retention -------------------------------------------------------------

    public async Task<int> RetentionDaysAsync() =>
        int.TryParse(await _settings.GetAsync(RetentionKey), out var d) && d is >= 7 and <= 3650 ? d : DefaultRetentionDays;

    /// <summary>
    /// Attachments, log excerpts and reply addresses of reports closed longer than
    /// the retention period are deleted; the report text and the notes stay.
    /// </summary>
    public async Task<int> PurgeAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-await RetentionDaysAsync());
        // set-based in the database (audit S1.3.1): attachment bytes are never loaded, and every
        // expired report is cleaned in one run, not 200
        var now = DateTime.UtcNow;
        var expired = _db.Feedbacks.Where(f => (f.Status == "done" || f.Status == "declined") && f.DoneAt != null && f.DoneAt < cutoff);
        var ids = expired.Select(f => f.Id);
        await _db.FeedbackAttachments.Where(a => ids.Contains(a.FeedbackId) && a.Data != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Data, (byte[]?)null).SetProperty(a => a.PurgedAt, now));
        return await expired.Where(f => f.LogExcerpt != null || f.Email != null || f.AttachmentCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LogExcerpt, (string?)null).SetProperty(f => f.Email, (string?)null)
                                      .SetProperty(f => f.AttachmentCount, 0));
    }
}

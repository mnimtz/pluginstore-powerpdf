using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AddonStore.Web.Services;

/// <summary>
/// Star ratings and problem reports from Add-on Store clients (S0.11.0).
/// Anonymous: the client sends a random per-installation id; only a per-package
/// hash of it is stored. Ratings are public as an average; feedback text is
/// visible to the package owner and admins only.
/// </summary>
public class FeedbackService
{
    public const int MaxMessage = 4000;
    public const int MaxLog = 20000;
    private static readonly Regex InstallIdRx = new(@"^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    public static readonly string[] Kinds = { "problem", "comment" };

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly NotificationService _notify;
    private readonly GeoService _geo;
    private readonly ILogger<FeedbackService> _log;

    public FeedbackService(AppDbContext db, IMemoryCache cache, NotificationService notify, GeoService geo, ILogger<FeedbackService> log)
    {
        _db = db; _cache = cache; _notify = notify; _geo = geo; _log = log;
    }

    public record Summary(double Average, int Count);
    public record Outcome(bool Ok, string Code, string Message);

    public static bool ValidInstallId(string? id) => id is not null && InstallIdRx.IsMatch(id);

    public static string InstallHash(string installId, string packageId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installId.Trim().ToLowerInvariant() + "|" + packageId))).ToLowerInvariant();

    /// <summary>Average and count per package, for catalog, web and reports.</summary>
    public static async Task<Dictionary<string, Summary>> SummariesAsync(AppDbContext db)
    {
        var rows = await db.Ratings.AsNoTracking()
            .GroupBy(r => r.PackageId)
            .Select(g => new { g.Key, Avg = g.Average(r => (double)r.Stars), N = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => new Summary(Math.Round(r.Avg, 1), r.N));
    }

    /// <summary>Simple sliding limit per key and day (in memory; enough against casual abuse).</summary>
    private bool Allow(string key, int perDay)
    {
        var k = "fb:" + key + ":" + DateTime.UtcNow.ToString("yyyyMMdd");
        var n = _cache.GetOrCreate(k, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1); return new int[1]; })!;
        lock (n) { if (n[0] >= perDay) return false; n[0]++; return true; }
    }

    private async Task<bool> IsPublishedAsync(string packageId) =>
        await _db.PackageVersions.AnyAsync(v => v.PackageId == packageId && (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta));

    public async Task<(Outcome Result, Summary? Summary)> RateAsync(HttpContext ctx, string packageId, string? installId, int stars, string? version)
    {
        if (!ValidInstallId(installId)) return (new(false, "INSTALL_ID_INVALID", "installId must be a GUID."), null);
        if (stars is < 1 or > 5) return (new(false, "STARS_INVALID", "stars must be 1 to 5."), null);
        if (!await IsPublishedAsync(packageId)) return (new(false, "PACKAGE_NOT_FOUND", $"No released package with id '{packageId}'."), null);
        var ip = GeoService.ClientIp(ctx)?.ToString() ?? "";
        if (!Allow("rate-ip:" + ip, 200)) return (new(false, "RATE_LIMITED", "Too many ratings from this network today."), null);

        var hash = InstallHash(installId!, packageId);
        var row = await _db.Ratings.FirstOrDefaultAsync(r => r.PackageId == packageId && r.InstallHash == hash);
        if (row is null)
        {
            row = new Rating { PackageId = packageId, InstallHash = hash };
            _db.Ratings.Add(row);
        }
        row.Stars = stars;
        row.Version = Clip(version, 32);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var sum = (await SummariesAsync(_db)).GetValueOrDefault(packageId);
        return (new(true, "OK", "Rating saved."), sum);
    }

    public async Task<Outcome> AddAsync(HttpContext ctx, string packageId, string? installId, string? kind, string? message,
                                        string? email, string? version, string? log)
    {
        if (!ValidInstallId(installId)) return new(false, "INSTALL_ID_INVALID", "installId must be a GUID.");
        kind = (kind ?? "problem").Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind)) return new(false, "FEEDBACK_KIND_INVALID", "kind must be 'problem' or 'comment'.");
        message = (message ?? "").Trim();
        if (message.Length < 5 || message.Length > MaxMessage)
            return new(false, "FEEDBACK_MESSAGE_INVALID", $"message must have 5 to {MaxMessage} characters.");
        email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (email is not null && (email.Length > 200 || !System.Net.Mail.MailAddress.TryCreate(email, out _)))
            return new(false, "FEEDBACK_EMAIL_INVALID", "email is not a valid address.");
        if (!await IsPublishedAsync(packageId)) return new(false, "PACKAGE_NOT_FOUND", $"No released package with id '{packageId}'.");
        var ip = GeoService.ClientIp(ctx)?.ToString() ?? "";
        if (!Allow("fb-install:" + installId!.ToLowerInvariant(), 10) || !Allow("fb-ip:" + ip, 50))
            return new(false, "RATE_LIMITED", "Too many reports today; please try again tomorrow.");

        var ua = ctx.Request.Headers.UserAgent.ToString();
        var fb = new Feedback
        {
            PackageId = packageId,
            Version = Clip(version, 32),
            Kind = kind,
            Message = message,
            Email = email,
            ClientInfo = Clip(ua, 300),
            LogExcerpt = string.IsNullOrWhiteSpace(log) ? null : Clip(log, MaxLog),
            Country = _geo.Lookup(ctx).Country,
            InstallHash = InstallHash(installId, packageId),
        };
        _db.Feedbacks.Add(fb);
        await _db.SaveChangesAsync();

        try
        {
            var pkg = await _db.Packages.Include(p => p.Owner).FirstOrDefaultAsync(p => p.Id == packageId);
            if (pkg?.Owner is not null)
            {
                static string enc(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
                var subject = kind == "problem" ? $"[Add-on Store] Problem report: {packageId} {fb.Version}"
                                                : $"[Add-on Store] Comment: {packageId} {fb.Version}";
                var text = $"<p>{(kind == "problem" ? "A user reported a problem" : "A user left a comment")} for <b>{enc(packageId)}</b> " +
                           $"version {enc(fb.Version)}:</p><blockquote style=\"white-space:pre-wrap\">{enc(message)}</blockquote>" +
                           (email is null ? "<p>No reply address was given.</p>" : $"<p>Reply to: <a href=\"mailto:{enc(email)}\">{enc(email)}</a></p>") +
                           $"<p style=\"color:#8094AA\">{enc(fb.ClientInfo)}{(fb.LogExcerpt is null ? "" : " · log excerpt attached in the portal")}</p>" +
                           await _notify.PluginLinkAsync(packageId);
                await _notify.NotifyUserAsync("Feedback", pkg.Owner, subject, text);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "feedback mail failed for {Package}", packageId); }
        return new(true, "OK", "Thank you, the report was sent to the developer.");
    }

    private static string Clip(string? v, int max)
    {
        v = (v ?? "").Trim();
        return v.Length > max ? v[..max] : v;
    }
}

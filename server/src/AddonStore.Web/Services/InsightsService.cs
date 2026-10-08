using System.Globalization;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// The developer dashboard (S1.1.0): downloads, versions, the Power PDF and
/// Windows versions of the users, ratings and problem reports of the add-ons a
/// user owns; admins see all add-ons and may narrow to one developer. Portal
/// (/Insights) and API (GET /api/insights) share this computation. Locations
/// stop at the country; city and network operator stay in the admin reports.
/// </summary>
public class InsightsService
{
    public static readonly int[] Periods = { 7, 30, 90, 365 };
    private readonly AppDbContext _db;
    public InsightsService(AppDbContext db) => _db = db;

    public record Share(string Key, int Count, double Percent);
    public record Day(DateOnly Date, int Downloads);
    public record VersionRow(string Version, string Status, int Downloads, DateTime SubmittedAt);
    public record AddonRow(string Id, string Name, string Owner, string? Live, string? Beta, bool PendingReview,
                           int Period, int Previous, int AllTime, double? Rating, int Ratings, int ActiveReports, int OpenReports,
                           List<VersionRow> Versions);
    public record Result(int Days, DateOnly From, DateOnly To, string? Package, string? OwnerId,
                         int Downloads, int DownloadsPrevious, int DownloadsAllTime, int LiveAddons, int PendingReviews,
                         double? Rating, int Ratings, int ActiveReports, int OpenReports,
                         List<Day> Daily, List<AddonRow> Addons,
                         List<Share> PowerPdf, List<Share> Windows, List<Share> Architectures, List<Share> Languages,
                         List<Share> Countries, List<Share> Channels, List<(string Id, string Name)> Developers,
                         List<(string Id, string Name)> Options);

    public async Task<Result> ComputeAsync(AppUser user, bool admin, int days, string? package, string? ownerId, string culture)
    {
        if (!Periods.Contains(days)) days = 30;
        var cmp = new SemVerComparer();
        var pq = _db.Packages.AsNoTracking().Include(p => p.Versions).Include(p => p.Owner)
                    .Where(p => p.Id != SubmissionService.ClientPackageId);
        if (!admin) { pq = pq.Where(p => p.OwnerId == user.Id); ownerId = null; }
        else if (!string.IsNullOrEmpty(ownerId)) pq = pq.Where(p => p.OwnerId == ownerId);
        var packages = await pq.ToListAsync();
        var developers = admin
            ? (await _db.Packages.AsNoTracking().Include(p => p.Owner).Where(p => p.Id != SubmissionService.ClientPackageId && p.Owner != null)
                    .Select(p => new { p.OwnerId, p.Owner!.DisplayName }).Distinct().ToListAsync())
                .Select(o => (o.OwnerId, o.DisplayName)).OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            : new List<(string, string)>();
        if (!string.IsNullOrEmpty(package) && !packages.Any(p => p.Id == package)) package = null;
        var ids = packages.Where(p => package is null || p.Id == package).Select(p => p.Id).ToList();

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(days - 1));
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(days - 1));
        string k(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string fromKey = k(from), toKey = k(to), prevFromKey = k(prevFrom);
        var stats = await _db.UsageStats.AsNoTracking()
            .Where(s => s.Kind == "download" && ids.Contains(s.PackageId) &&
                        string.Compare(s.Day, prevFromKey) >= 0 && string.Compare(s.Day, toKey) <= 0)
            .ToListAsync();
        var now = stats.Where(s => string.Compare(s.Day, fromKey) >= 0).ToList();
        var prev = stats.Where(s => string.Compare(s.Day, fromKey) < 0).ToList();

        var ratings = await _db.Ratings.AsNoTracking().Where(r => ids.Contains(r.PackageId))
            .GroupBy(r => r.PackageId).Select(g => new { g.Key, Sum = g.Sum(r => r.Stars), N = g.Count() }).ToListAsync();
        var reports = await _db.Feedbacks.AsNoTracking().Where(f => ids.Contains(f.PackageId) && f.Status != "done" && f.Status != "declined")
            .GroupBy(f => f.PackageId).Select(g => new { g.Key, Active = g.Count(), Open = g.Count(f => f.Status == "open") }).ToListAsync();

        var addons = new List<AddonRow>();
        foreach (var p in packages.Where(p => ids.Contains(p.Id)))
        {
            var live = p.Versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var beta = p.Versions.Where(v => v.Status == VersionStatus.Beta).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var waiting = p.Versions.Any(v => v.Status == VersionStatus.Submitted);   // S1.6.0
            var newest = p.Versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var r = ratings.FirstOrDefault(x => x.Key == p.Id);
            var rep = reports.FirstOrDefault(x => x.Key == p.Id);
            addons.Add(new AddonRow(p.Id, CatalogUi.DisplayName(p, live ?? newest, culture), p.Owner?.DisplayName ?? "",
                // private add-ons need no approval (S1.4.0): they never wait for one
                live?.Version, beta?.Version, p.Visibility != "private" && waiting,
                now.Where(s => s.PackageId == p.Id).Sum(s => s.Count), prev.Where(s => s.PackageId == p.Id).Sum(s => s.Count),
                p.Versions.Sum(v => v.Downloads), r is null ? null : Math.Round((double)r.Sum / r.N, 1), r?.N ?? 0,
                rep?.Active ?? 0, rep?.Open ?? 0,
                p.Versions.OrderByDescending(v => v.Version, cmp)
                    .Select(v => new VersionRow(v.Version, v.Status.ToString().ToLowerInvariant(), v.Downloads, v.SubmittedAt)).ToList()));
        }
        addons.Sort((a, b) => b.Period != a.Period ? b.Period.CompareTo(a.Period) : b.AllTime.CompareTo(a.AllTime));

        var byDay = now.GroupBy(s => s.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var daily = new List<Day>();
        for (var d = from; d <= to; d = d.AddDays(1)) daily.Add(new Day(d, byDay.GetValueOrDefault(k(d))));

        static List<Share> Shares(IEnumerable<(string Key, int Count)> items)
        {
            var list = items.GroupBy(i => i.Key).Select(g => (g.Key, Count: g.Sum(x => x.Count))).Where(x => x.Count > 0)
                            .OrderByDescending(x => x.Count).ToList();
            var total = Math.Max(1, list.Sum(x => x.Count));
            return list.Select(x => new Share(x.Key, x.Count, Math.Round(100.0 * x.Count / total, 1))).ToList();
        }
        var client = now.Where(s => s.Source == "client").ToList();
        var ratingN = ratings.Sum(r => r.N);
        return new Result(days, from, to, package, ownerId,
            now.Sum(s => s.Count), prev.Sum(s => s.Count), addons.Sum(a => a.AllTime),
            addons.Count(a => a.Live is not null), addons.Count(a => a.PendingReview),
            ratingN == 0 ? null : Math.Round((double)ratings.Sum(r => r.Sum) / ratingN, 1), ratingN,
            addons.Sum(a => a.ActiveReports), addons.Sum(a => a.OpenReports),
            daily, addons,
            Shares(client.Select(s => (s.HostVersion == "" ? "?" : s.HostVersion, s.Count))),
            Shares(client.Select(s => (s.OsVersion == "" ? "?" : Pages.Admin.ReportsModel.WindowsName(s.OsVersion), s.Count))),
            Shares(client.Select(s => (s.Arch == "" ? "?" : s.Arch, s.Count))),
            Shares(client.Select(s => (s.Lang == "" ? "?" : s.Lang.ToLowerInvariant(), s.Count))),
            Shares(now.Select(s => (s.Country == "" ? "?" : s.Country, s.Count))),
            Shares(now.Select(s => (s.Source == "" ? "?" : s.Source, s.Count))),
            developers,
            packages.Select(p => (p.Id, CatalogUi.DisplayName(p, p.Versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault(), culture)))
                    .OrderBy(o => o.Item2, StringComparer.CurrentCultureIgnoreCase).ToList());
    }
}

using System.Globalization;
using System.Text;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// Admin reports: downloads per add-on and over time, clients in use, catalog
/// languages, submissions and reviews, developers, source code coverage.
/// Time series come from the anonymous UsageStats table (since S0.9.0); the
/// all-time totals from the per-version download counters.
/// </summary>
public class ReportsModel : PageModel
{
    public static readonly int[] Periods = { 7, 30, 90, 365 };

    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly UsageService _usage;
    private readonly AuditService _audit;
    private readonly GeoService _geo;

    public ReportsModel(AppDbContext db, UserManager<AppUser> users, UsageService usage,
                        AuditService audit, GeoService geo)
    {
        _db = db; _users = users; _usage = usage; _audit = audit; _geo = geo;
    }

    [BindProperty(SupportsGet = true)] public int Days { get; set; } = 30;
    /// <summary>Custom range (both set, From &lt;= To); overrides Days. Max two years.</summary>
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    /// <summary>Optional add-on filter for downloads, locations and IP events.</summary>
    [BindProperty(SupportsGet = true)] public string? Pkg { get; set; }
    /// <summary>Optional country filter (ISO code) for all counters and IP events.</summary>
    [BindProperty(SupportsGet = true)] public string? Country { get; set; }
    /// <summary>Optional source filter: client, web, api.</summary>
    [BindProperty(SupportsGet = true)] public string? Src { get; set; }
    /// <summary>Report language (any of the 16 UI languages); applied by the localization middleware.</summary>
    [BindProperty(SupportsGet = true)] public string? Rlang { get; set; }
    public static readonly string[] ReportLanguages = AddonStore.Web.Services.Lang.Ui;
    /// <summary>Print view for "Save as PDF" (all sections, all rows).</summary>
    [BindProperty(SupportsGet = true)] public bool Print { get; set; }

    /// <summary>Report sections of the left navigation (S0.17.0); one is shown at a time.</summary>
    public static readonly (string Key, string Label)[] Views =
    {
        ("overview", "Overview"), ("addons", "Add-ons"), ("links", "Shared links"), ("clients", "Clients"),
        ("locations", "Locations"), ("submissions", "Submissions and reviews"), ("developers", "Developers"),
        ("sources", "Source code"), ("ip", "IP address logging"),
    };
    [BindProperty(SupportsGet = true)] public string? View { get; set; }
    /// <summary>Rows per page of the long lists; the page of each list is p_&lt;list&gt; in the query.</summary>
    public static readonly int[] PageSizes = { 25, 50, 100 };
    [BindProperty(SupportsGet = true)] public int Size { get; set; } = 25;
    public bool Show(string view) => Print || View == view;

    public int PageCount(int total) => Math.Max(1, (total + Size - 1) / Size);
    /// <summary>Current page of a list (1-based, clamped to the list).</summary>
    public int PageOf(string key, int total) =>
        Math.Min(int.TryParse(Request.Query["p_" + key], out var pg) && pg > 0 ? pg : 1, PageCount(total));
    /// <summary>The rows of the current page; the print view gets all rows.</summary>
    public IEnumerable<T> PageItems<T>(IReadOnlyList<T> list, string key) =>
        Print ? list : list.Skip((PageOf(key, list.Count) - 1) * Size).Take(Size);

    public DateOnly RangeFrom { get; private set; }
    public DateOnly RangeTo { get; private set; }
    public bool CustomRange => From is not null;
    /// <summary>Bar bucket: "day" up to 62 days, "week" up to 366, else "month".</summary>
    public string Granularity { get; private set; } = "day";
    public List<(string Id, string Name)> PackageOptions { get; } = new();
    public string? PkgName { get; private set; }
    public List<string> CountryOptions { get; } = new();
    public DateOnly PrevFrom { get; private set; }
    public DateOnly PrevTo { get; private set; }
    public bool AnyFilter => !string.IsNullOrEmpty(Pkg) || !string.IsNullOrEmpty(Country) || !string.IsNullOrEmpty(Src);
    public DateTime GeneratedAt { get; } = DateTime.UtcNow;

    /// <summary>Query values that keep the current range and filter in links.</summary>
    public Dictionary<string, string?> RouteValues(bool print = false) => new()
    {
        ["days"] = CustomRange ? null : Days.ToString(CultureInfo.InvariantCulture),
        ["from"] = From?.ToString("yyyy-MM-dd"),
        ["to"] = To?.ToString("yyyy-MM-dd"),
        ["pkg"] = string.IsNullOrEmpty(Pkg) ? null : Pkg,
        ["country"] = string.IsNullOrEmpty(Country) ? null : Country,
        ["src"] = string.IsNullOrEmpty(Src) ? null : Src,
        ["rlang"] = Rlang is not null && ReportLanguages.Contains(Rlang) ? Rlang : null,
        ["print"] = print ? "true" : null,
        ["view"] = print || View == "overview" ? null : View,
        ["size"] = print || Size == PageSizes[0] ? null : Size.ToString(CultureInfo.InvariantCulture),
    };

    private (DateOnly From, DateOnly To) ResolveRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!Periods.Contains(Days)) Days = 30;
        if (From is { } f && To is { } t && f <= t)
        {
            if (t > today) t = today;
            if (f > t) f = t;
            if (t.DayNumber - f.DayNumber > 730) f = t.AddDays(-730);
            From = f; To = t;
            return (f, t);
        }
        From = null; To = null;
        return (today.AddDays(-(Days - 1)), today);
    }

    private static DateOnly Bucket(DateOnly d, string granularity) => granularity switch
    {
        "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
        "month" => new DateOnly(d.Year, d.Month, 1),
        _ => d
    };

    public record Kpis(int DownloadsAllTime, int DownloadsPeriod, int CatalogPeriod, int ClientMsiPeriod,
                       int LiveAddons, int Developers, int PendingReviews,
                       int DownloadsPrev = 0, int CatalogPrev = 0, int ClientMsiPrev = 0);

    /// <summary>Change against the previous period of the same length, null without a base.</summary>
    public static double? Delta(int now, int prev) => prev == 0 ? (now == 0 ? 0 : null) : 100.0 * (now - prev) / prev;
    public record DayBar(DateOnly Day, int Downloads, int Catalog);
    public record AddonRow(string Id, string Name, string Owner, string? LiveVersion, int AllTime, int Period,
                           int ViaClient, int ViaWeb, int ViaApi, List<(string Version, VersionStatus Status, int Downloads)> Versions)
    {
        public int Rank { get; set; }
        public int Prev { get; set; }
        public int? PrevRank { get; set; }
        public double Share { get; set; }
        public int[] Spark { get; set; } = Array.Empty<int>();
        public string TopCountry { get; set; } = "";
    }
    public record Share(string Key, int Count, double Percent);
    public record MonthRow(string Month, int Submitted, int Approved, int Rejected, double? MedianReviewHours);
    public record DevRow(string Name, string Email, string Role, int Packages, int Versions, int Downloads, DateTime? LastSubmission);
    public record SourceGap(string Id, string Name, string Version, VersionStatus Status);
    /// <summary>Shared add-on links (/a/{slug}?ref=) per ref and add-on in the period.</summary>
    public record ShareRow(string Ref, string Id, string Name, int Views, int Installs, int Clients);

    public Kpis K { get; private set; } = new(0, 0, 0, 0, 0, 0, 0);
    public List<DayBar> Daily { get; } = new();
    public int DailyMax { get; private set; }
    public List<AddonRow> Addons { get; } = new();
    public List<Share> ClientVersions { get; } = new();
    public List<Share> Languages { get; } = new();
    public List<Share> Sources { get; } = new();
    public List<Share> Countries { get; } = new();
    public List<Share> Cities { get; } = new();
    public List<Share> Orgs { get; } = new();
    public List<Share> HostVersions { get; } = new();
    public List<Share> OsVersions { get; } = new();
    public List<Share> Archs { get; } = new();
    public DateTime? GeoDate => _geo.DatabaseDate;

    // IP logging (GDPR: off by default, admin confirmation required)
    public bool IpOn { get; private set; }
    public int RetentionDays { get; private set; } = UsageService.DefaultRetentionDays;
    public int IpEventCount { get; private set; }
    public List<UsageEvent> RecentEvents { get; } = new();
    /// <summary>IP events in the period and filter (RecentEvents holds the current page).</summary>
    public int EventsTotal { get; private set; }
    public List<Share> TopIps { get; } = new();
    public List<MonthRow> Months { get; } = new();
    public List<DevRow> Devs { get; } = new();
    public List<SourceGap> SourceGaps { get; } = new();
    public List<ShareRow> ShareLinks { get; } = new();
    public DateOnly? TrackingSince { get; private set; }

    public async Task OnGetAsync()
    {
        var culture = AddonStore.Web.Services.Lang.Current;
        if (!Views.Any(v => v.Key == View)) View = "overview";
        if (!PageSizes.Contains(Size)) Size = PageSizes[0];
        var (from, to) = ResolveRange();
        RangeFrom = from; RangeTo = to;
        var span = to.DayNumber - from.DayNumber + 1;
        Granularity = span <= 62 ? "day" : span <= 366 ? "week" : "month";
        string fromKey = from.ToString("yyyy-MM-dd"), toKey = to.ToString("yyyy-MM-dd");

        PrevTo = from.AddDays(-1);
        PrevFrom = PrevTo.AddDays(-(span - 1));
        string prevFromKey = PrevFrom.ToString("yyyy-MM-dd"), prevToKey = PrevTo.ToString("yyyy-MM-dd");

        CountryOptions.AddRange(await _db.UsageStats.AsNoTracking().Where(s => s.Country != "")
            .Select(s => s.Country).Distinct().OrderBy(c => c).ToListAsync());
        if (Src is not ("client" or "web" or "api")) Src = null;
        if (!string.IsNullOrEmpty(Country) && !CountryOptions.Contains(Country)) Country = null;
        bool Match(UsageStat s) => (Country is null || s.Country == Country) && (Src is null || s.Source == Src);

        var stats = (await _db.UsageStats.AsNoTracking()
            .Where(s => string.Compare(s.Day, fromKey) >= 0 && string.Compare(s.Day, toKey) <= 0).ToListAsync())
            .Where(Match).ToList();
        var prevStats = (await _db.UsageStats.AsNoTracking()
            .Where(s => string.Compare(s.Day, prevFromKey) >= 0 && string.Compare(s.Day, prevToKey) <= 0).ToListAsync())
            .Where(Match).ToList();
        var first = await _db.UsageStats.AsNoTracking().OrderBy(s => s.Day).Select(s => s.Day).FirstOrDefaultAsync();
        if (first is not null && DateOnly.TryParseExact(first, "yyyy-MM-dd", out var f)) TrackingSince = f;

        var packages = await _db.Packages.AsNoTracking().Include(p => p.Owner).Include(p => p.Versions).ToListAsync();
        var versions = packages.SelectMany(p => p.Versions).ToList();
        var cmp = new SemVerComparer();
        foreach (var p in packages.Where(p => p.Id != SubmissionService.ClientPackageId))
        {
            var v = p.Versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault()
                    ?? p.Versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            PackageOptions.Add((p.Id, CatalogUi.DisplayName(p, v, culture)));
        }
        PackageOptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrEmpty(Pkg))
        {
            PkgName = PackageOptions.FirstOrDefault(o => o.Id == Pkg).Name;
            if (PkgName is null) Pkg = null;
        }
        var filtered = string.IsNullOrEmpty(Pkg);
        var dl = stats.Where(s => s.Kind == "download" && (filtered || s.PackageId == Pkg)).ToList();
        var dlPrev = prevStats.Where(s => s.Kind == "download" && (filtered || s.PackageId == Pkg)).ToList();
        // Locations and networks: all counted requests, or only the add-on's downloads when filtered.
        var geoBase = filtered ? stats : dl;

        // KPIs
        var developerRole = await _db.Roles.Where(r => r.Name == SchemaUpgrade.DefaultRole).Select(r => r.Id).FirstOrDefaultAsync();
        var adminRole = await _db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstOrDefaultAsync();
        var reviewerRole = await _db.Roles.Where(r => r.Name == "Reviewer").Select(r => r.Id).FirstOrDefaultAsync();
        var activeUsers = await _db.Users.AsNoTracking().Where(u => u.Status == UserStatus.Active).ToListAsync();
        var userRoles = await _db.UserRoles.AsNoTracking().ToListAsync();
        K = new Kpis(
            versions.Sum(v => v.Downloads),
            dl.Sum(s => s.Count),
            stats.Where(s => s.Kind == "catalog").Sum(s => s.Count),
            stats.Where(s => s.Kind == "msi").Sum(s => s.Count),
            packages.Count(p => p.Id != SubmissionService.ClientPackageId && p.Versions.Any(v => v.Status == VersionStatus.Live)),
            activeUsers.Count(u => userRoles.Any(r => r.UserId == u.Id && r.RoleId == developerRole) ||
                                   packages.Any(p => p.OwnerId == u.Id && p.Id != SubmissionService.ClientPackageId)),
            packages.Count(p =>
            {
                var live = p.Versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
                var beta = p.Versions.Where(v => v.Status == VersionStatus.Beta).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
                return beta is not null && (live is null || cmp.Compare(beta.Version, live.Version) > 0);
            }),
            dlPrev.Sum(s => s.Count),
            prevStats.Where(s => s.Kind == "catalog").Sum(s => s.Count),
            prevStats.Where(s => s.Kind == "msi").Sum(s => s.Count));

        // Bars per day, week or month
        DateOnly KeyOf(string day) => Bucket(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), Granularity);
        var dlBy = dl.GroupBy(s => KeyOf(s.Day)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var catBy = stats.Where(s => s.Kind == "catalog").GroupBy(s => KeyOf(s.Day)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        for (var d = Bucket(from, Granularity); d <= to;
             d = Granularity switch { "week" => d.AddDays(7), "month" => d.AddMonths(1), _ => d.AddDays(1) })
            Daily.Add(new DayBar(d, dlBy.GetValueOrDefault(d), catBy.GetValueOrDefault(d)));
        DailyMax = Math.Max(1, Daily.Max(b => Math.Max(b.Downloads, b.Catalog)));

        // Per add-on
        foreach (var p in packages.Where(p => p.Id != SubmissionService.ClientPackageId && (filtered || p.Id == Pkg)))
        {
            var newest = p.Versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var live = p.Versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var mine = dl.Where(s => s.PackageId == p.Id).ToList();
            Addons.Add(new AddonRow(p.Id, CatalogUi.DisplayName(p, live ?? newest, culture), p.Owner?.DisplayName ?? "",
                live?.Version, p.Versions.Sum(v => v.Downloads), mine.Sum(s => s.Count),
                mine.Where(s => s.Source == "client").Sum(s => s.Count),
                mine.Where(s => s.Source == "web").Sum(s => s.Count),
                mine.Where(s => s.Source == "api").Sum(s => s.Count),
                p.Versions.OrderByDescending(v => v.Version, cmp)
                    .Select(v => (v.Version, v.Status, v.Downloads)).ToList()));
        }
        Addons.Sort((a, b) => b.Period != a.Period ? b.Period.CompareTo(a.Period) : b.AllTime.CompareTo(a.AllTime));
        var prevBy = dlPrev.GroupBy(s => s.PackageId).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var prevRanks = prevBy.Where(x => x.Value > 0).OrderByDescending(x => x.Value)
                              .Select((x, i) => (x.Key, Rank: i + 1)).ToDictionary(x => x.Key, x => x.Rank);
        var periodTotal = Math.Max(1, Addons.Sum(a => a.Period));
        var buckets = Daily.Select(b => b.Day).ToList();
        for (var i = 0; i < Addons.Count; i++)
        {
            var a = Addons[i];
            a.Rank = i + 1;
            a.Prev = prevBy.GetValueOrDefault(a.Id);
            a.PrevRank = prevRanks.TryGetValue(a.Id, out var pr) ? pr : null;
            a.Share = 100.0 * a.Period / periodTotal;
            var mine = dl.Where(s => s.PackageId == a.Id).ToList();
            var byBucket = mine.GroupBy(s => KeyOf(s.Day)).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
            a.Spark = buckets.Select(b => byBucket.GetValueOrDefault(b)).ToArray();
            a.TopCountry = mine.Where(s => s.Country != "").GroupBy(s => s.Country)
                               .OrderByDescending(g => g.Sum(x => x.Count)).Select(g => g.Key).FirstOrDefault() ?? "";
        }

        // Shares
        static List<Share> Shares(IEnumerable<(string Key, int Count)> items)
        {
            var list = items.GroupBy(i => i.Key).Select(g => (Key: g.Key, Count: g.Sum(x => x.Count)))
                            .Where(x => x.Count > 0).OrderByDescending(x => x.Count).ToList();
            var total = Math.Max(1, list.Sum(x => x.Count));
            return list.Select(x => new Share(x.Key, x.Count, 100.0 * x.Count / total)).ToList();
        }
        var catalog = stats.Where(s => s.Kind == "catalog").ToList();
        ClientVersions.AddRange(Shares(catalog.Select(s => (s.ClientVersion == "" ? "?" : s.ClientVersion, s.Count)))
                                .OrderByDescending(s => s.Key == "?" ? "" : s.Key, cmp));
        Languages.AddRange(Shares(catalog.Select(s => (s.Lang == "" ? "?" : s.Lang.ToLowerInvariant(), s.Count))));
        Sources.AddRange(Shares(dl.Select(s => (s.Source, s.Count))));
        // Country over all counted requests (downloads, catalog, MSI); client details from catalog fetches.
        Countries.AddRange(Shares(geoBase.Select(s => (s.Country == "" ? "?" : s.Country, s.Count))));
        Cities.AddRange(Shares(geoBase.Select(s => (s.City == "" ? "?" : s.City + (s.Region != "" && s.Region != s.City ? ", " + s.Region : "") +
                                                                 (s.Country != "" ? " (" + s.Country + ")" : ""), s.Count))));
        Orgs.AddRange(Shares(geoBase.Select(s => (s.Org == "" ? "?" : s.Org, s.Count))));
        var fromClient = catalog.Where(s => s.Source == "client").ToList();
        HostVersions.AddRange(Shares(fromClient.Select(s => (s.HostVersion == "" ? "?" : s.HostVersion, s.Count))));
        OsVersions.AddRange(Shares(fromClient.Select(s => (s.OsVersion == "" ? "?" : WindowsName(s.OsVersion), s.Count))));
        Archs.AddRange(Shares(fromClient.Select(s => (s.Arch == "" ? "?" : s.Arch, s.Count))));

        // Shared links: views, install clicks and client downloads per ref and add-on
        var shareStats = await _db.ShareStats.AsNoTracking()
            .Where(s => string.Compare(s.Day, fromKey) >= 0 && string.Compare(s.Day, toKey) <= 0).ToListAsync();
        var names = PackageOptions.ToDictionary(o => o.Id, o => o.Name);
        ShareLinks.AddRange(shareStats.Where(s => filtered || s.PackageId == Pkg)
            .GroupBy(s => (s.Ref, s.PackageId))
            .Select(g => new ShareRow(g.Key.Ref, g.Key.PackageId, names.GetValueOrDefault(g.Key.PackageId, g.Key.PackageId),
                g.Where(x => x.Kind == "view").Sum(x => x.Count), g.Where(x => x.Kind == "install").Sum(x => x.Count),
                g.Where(x => x.Kind == "client").Sum(x => x.Count)))
            .OrderByDescending(r => r.Views + r.Installs * 3 + r.Clients * 3));

        // IP logging
        IpOn = await _usage.IpLoggingOnAsync();
        RetentionDays = await _usage.RetentionDaysAsync();
        IpEventCount = await _db.UsageEvents.CountAsync();
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var events = _db.UsageEvents.AsNoTracking().Where(e => e.At >= fromUtc && e.At < toUtc && (filtered || e.PackageId == Pkg) &&
                                                         (Country == null || e.Country == Country) && (Src == null || e.Source == Src));
        // Paged in the database: the table can hold many thousand requests.
        if (Show("ip"))
        {
            EventsTotal = await events.CountAsync();
            var skip = Print ? 0 : (PageOf("events", EventsTotal) - 1) * Size;
            RecentEvents.AddRange(await events.OrderByDescending(e => e.Id).Skip(skip).Take(Print ? 100 : Size).ToListAsync());
            TopIps.AddRange(Shares((await events
                .GroupBy(e => e.Ip).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                .Select(x => (x.Key, x.N))));
        }

        // Submissions and reviews per month (last 12 months, independent of the period)
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-11);
        var subs = versions.Where(v => v.PackageId != SubmissionService.ClientPackageId).ToList();
        for (var m = monthStart; m <= DateTime.UtcNow; m = m.AddMonths(1))
        {
            var end = m.AddMonths(1);
            var reviewed = subs.Where(v => v.ReviewedAt >= m && v.ReviewedAt < end).ToList();
            var hours = reviewed.Select(v => (v.ReviewedAt!.Value - v.SubmittedAt).TotalHours).Where(h => h >= 0)
                                .OrderBy(h => h).ToList();
            Months.Add(new MonthRow(m.ToString("yyyy-MM"),
                subs.Count(v => v.SubmittedAt >= m && v.SubmittedAt < end),
                reviewed.Count(v => v.Status is VersionStatus.Live or VersionStatus.Withdrawn),
                reviewed.Count(v => v.Status == VersionStatus.Rejected),
                hours.Count == 0 ? null : hours[hours.Count / 2]));
        }

        // Developers
        foreach (var u in activeUsers)
        {
            var own = packages.Where(p => p.OwnerId == u.Id && p.Id != SubmissionService.ClientPackageId).ToList();
            var roleIds = userRoles.Where(r => r.UserId == u.Id).Select(r => r.RoleId).ToList();
            var role = adminRole is not null && roleIds.Contains(adminRole) ? "Admin" :
                       reviewerRole is not null && roleIds.Contains(reviewerRole) ? "Reviewer" : SchemaUpgrade.DefaultRole;
            if (own.Count == 0 && role != SchemaUpgrade.DefaultRole) continue;
            var vs = own.SelectMany(p => p.Versions).ToList();
            Devs.Add(new DevRow(u.DisplayName, u.Email ?? "", role, own.Count, vs.Count, vs.Sum(v => v.Downloads),
                                vs.Count == 0 ? null : vs.Max(v => v.SubmittedAt)));
        }
        Devs.Sort((a, b) => b.Downloads != a.Downloads ? b.Downloads.CompareTo(a.Downloads) : b.Packages.CompareTo(a.Packages));

        // Live or beta versions without deposited source code
        foreach (var p in packages.Where(p => p.Id != SubmissionService.ClientPackageId))
            foreach (var v in p.Versions.Where(v => v.Status is VersionStatus.Live or VersionStatus.Beta && v.SourcePath is null))
                SourceGaps.Add(new SourceGap(p.Id, CatalogUi.DisplayName(p, v, culture), v.Version, v.Status));
    }

    /// <summary>Windows 10 and 11 share major 10; build 22000+ is Windows 11.</summary>
    public static string WindowsName(string v)
    {
        var parts = v.Split('.');
        if (parts.Length >= 3 && parts[0] == "10" && int.TryParse(parts[2], out var build))
            return (build >= 22000 ? "Windows 11 (" : "Windows 10 (") + build + ")";
        return "Windows " + v;
    }

    /// <summary>Stored IP events of the period as CSV; every export is audited.</summary>
    public async Task<IActionResult> OnGetIpCsvAsync()
    {
        var (from, to) = ResolveRange();
        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await _db.UsageEvents.AsNoTracking()
            .Where(e => e.At >= fromUtc && e.At < toUtc && (string.IsNullOrEmpty(Pkg) || e.PackageId == Pkg) &&
                        (string.IsNullOrEmpty(Country) || e.Country == Country) && (string.IsNullOrEmpty(Src) || e.Source == Src))
            .OrderBy(e => e.Id).ToListAsync();
        var me = await _users.GetUserAsync(User);
        await _audit.LogAsync(me!.DisplayName, "usage.iplogging.exported", "IP logging", $"{rows.Count} events, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        // Quote every cell; neutralise formula prefixes (CSV injection in spreadsheet apps).
        static string Cell(string v) =>
            "\"" + (v.Length > 0 && "=+-@\t\r".Contains(v[0]) ? "'" + v : v).Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("time;kind;package;version;source;language;clientVersion;powerPdf;windows;arch;country;region;city;latitude;longitude;asn;organisation;ip;hostname;userAgent\r\n");
        foreach (var e in rows)
            sb.Append(string.Join(';', new[] { e.At.ToString("o"), e.Kind, e.PackageId, e.Version, e.Source, e.Lang,
                e.ClientVersion, e.HostVersion, e.OsVersion, e.Arch, e.Country, e.Region, e.City,
                e.Latitude?.ToString(CultureInfo.InvariantCulture) ?? "", e.Longitude?.ToString(CultureInfo.InvariantCulture) ?? "",
                e.Asn?.ToString(CultureInfo.InvariantCulture) ?? "", e.Org, e.Ip, e.Hostname ?? "", e.UserAgent }.Select(Cell)))
              .Append("\r\n");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"addonstore-ip-events-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
    }

    /// <summary>Raw daily counters of the period as CSV (semicolon, UTF-8 with BOM for Excel).</summary>
    public async Task<IActionResult> OnGetCsvAsync()
    {
        var (from, to) = ResolveRange();
        string fromKey = from.ToString("yyyy-MM-dd"), toKey = to.ToString("yyyy-MM-dd");
        var rows = await _db.UsageStats.AsNoTracking()
            .Where(s => string.Compare(s.Day, fromKey) >= 0 && string.Compare(s.Day, toKey) <= 0 &&
                        (string.IsNullOrEmpty(Pkg) || s.PackageId == Pkg) &&
                        (string.IsNullOrEmpty(Country) || s.Country == Country) && (string.IsNullOrEmpty(Src) || s.Source == Src))
            .OrderBy(s => s.Day).ThenBy(s => s.Kind).ThenBy(s => s.PackageId).ToListAsync();
        var sb = new StringBuilder("day;kind;package;version;source;language;clientVersion;powerPdf;windows;arch;country;count\r\n");
        foreach (var r in rows)
            sb.Append($"{r.Day};{r.Kind};{r.PackageId};{r.Version};{r.Source};{r.Lang};{r.ClientVersion};" +
                      $"{r.HostVersion};{r.OsVersion};{r.Arch};{r.Country};{r.Count}\r\n");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"addonstore-usage-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
    }
}

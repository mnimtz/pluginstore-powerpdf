using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services.GitScan;

/// <summary>
/// "Git & Veracode" (S2.8.0): connection to a git server, links add-on -> repository, webhook deliveries and the
/// connection tests. Step 1 of docs/konzept-git-veracode.md: connect, link and receive; the import of built and
/// scanned versions, scans on a schedule and automatic approval follow in later steps.
/// </summary>
public class GitScanService
{
    /// <summary>Link target of the store server (the store client is the package com.tungsten.pluginstore).</summary>
    public const string ServerTarget = "@store-server";
    public const int MaxEvents = 1000;
    public const int MaxWebhookBytes = 1024 * 1024;

    private readonly AppDbContext _db;
    private readonly GitScanConfig _config;
    private readonly AuditService _audit;
    private readonly IHttpClientFactory _http;

    public GitScanService(AppDbContext db, GitScanConfig config, AuditService audit, IHttpClientFactory http)
    {
        _db = db; _config = config; _audit = audit; _http = http;
    }

    public const string HttpName = "gitscan";

    public async Task<GitConnection> ConnectionAsync()
    {
        var g = await _config.GitAsync();
        return new GitConnection(g.ApiUrl, g.AuthMode, g.AppId, g.InstallationId,
            await _config.SecretAsync(GitScanConfig.AppKeyKey), await _config.SecretAsync(GitScanConfig.TokenKey), g.Owner);
    }

    // ---- tests ----------------------------------------------------------------------------------------------

    public async Task<GitTestResult> TestGitAsync(string actor, CancellationToken ct)
    {
        var g = await _config.GitAsync();
        var provider = GitProviders.Find(g.Provider)!;
        GitTestResult r;
        if (!GitScanConfig.ApiUrlAllowed(g.ApiUrl, out var why)) r = new(false, new() { why });
        else r = await provider.TestAsync(await ConnectionAsync(), _http.CreateClient(HttpName), ct);
        await _config.SetAsync(GitScanConfig.LastTestKey, $"{(r.Ok ? "ok" : "failed")}|{DateTime.UtcNow:O}|{r.Lines.FirstOrDefault()}");
        await LogEventAsync("test-git", "test", "", "", r.Ok ? "ok" : "failed", string.Join("; ", r.Lines));
        await _audit.LogAsync(actor, "gitscan.test", "git", r.Ok ? "ok" : "failed: " + r.Lines.FirstOrDefault());
        return r;
    }

    public async Task<GitTestResult> TestVeracodeAsync(string actor, CancellationToken ct)
    {
        var v = await _config.VeracodeAsync();
        var key = await _config.SecretAsync(GitScanConfig.VcApiKeyKey) ?? "";
        var r = await VeracodeClient.TestAsync(_http.CreateClient(VeracodeClient.HttpName), v.Host, v.ApiId, key, v.Policy, ct);
        await _config.SetAsync(GitScanConfig.VcLastTestKey, $"{(r.Ok ? "ok" : "failed")}|{DateTime.UtcNow:O}|{r.Lines.FirstOrDefault()}");
        await LogEventAsync("test-veracode", "test", "", "", r.Ok ? "ok" : "failed", string.Join("; ", r.Lines));
        await _audit.LogAsync(actor, "gitscan.test", "veracode", r.Ok ? "ok" : "failed: " + r.Lines.FirstOrDefault());
        return r;
    }

    public async Task<List<string>> ReposAsync(CancellationToken ct)
    {
        var g = await _config.GitAsync();
        if (!g.Enabled || !GitScanConfig.ApiUrlAllowed(g.ApiUrl, out _)) return new();
        return await GitProviders.Find(g.Provider)!.ListReposAsync(await ConnectionAsync(), _http.CreateClient(HttpName), ct);
    }

    // ---- links ----------------------------------------------------------------------------------------------

    public static readonly Regex RepoPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}/[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);
    public static readonly Regex TagPatternRule = new(@"^[A-Za-z0-9._/*-]{1,60}$", RegexOptions.Compiled);
    public static readonly Regex WorkflowRule = new(@"^\.github/workflows/[A-Za-z0-9._-]{1,80}\.ya?ml$", RegexOptions.Compiled);
    public static readonly Regex PathRule = new(@"^[A-Za-z0-9._-]{1,60}(/[A-Za-z0-9._-]{1,60}){0,5}$", RegexOptions.Compiled);
    public static readonly string[] AutoModes = { "default", "off", "beta", "live" };

    public record LinkInput(int? Id, string Target, string Repo, string Path, string TagPattern, string Workflow, string AutoRelease);

    public async Task<(bool Ok, string Message)> SaveLinkAsync(LinkInput i, string actor)
    {
        var target = (i.Target ?? "").Trim();
        var repo = (i.Repo ?? "").Trim().Trim('/');
        var path = (i.Path ?? "").Trim().Trim('/');
        var tag = (i.TagPattern ?? "").Trim();
        var wf = (i.Workflow ?? "").Trim();
        var auto = AutoModes.Contains(i.AutoRelease) ? i.AutoRelease : "default";
        if (target != ServerTarget && !await _db.Packages.AnyAsync(p => p.Id == target)) return (false, "Choose an add-on.");
        if (!RepoPattern.IsMatch(repo) || repo.Contains("..")) return (false, "Enter the repository as owner/name.");
        if (path.Length > 0 && (!PathRule.IsMatch(path) || path.Split('/').Any(s => s is "." or ".."))) return (false, "The folder is a relative path inside the repository, e.g. measuretools.");
        if (tag.Length == 0) tag = "v*";
        if (!TagPatternRule.IsMatch(tag)) return (false, "The tag pattern uses letters, digits, . _ - / and *, e.g. v* or measuretools-v*.");
        if (wf.Length == 0) wf = ".github/workflows/addon-release.yml";
        if (!WorkflowRule.IsMatch(wf)) return (false, "The workflow is a file under .github/workflows/ ending in .yml.");
        if ((target == ServerTarget || target == SubmissionService.ClientPackageId) && auto is "beta" or "live")
            return (false, "The store itself is never approved automatically.");

        GitScanLink? row = i.Id is { } id ? await _db.GitScanLinks.FirstOrDefaultAsync(l => l.Id == id) : null;
        if (i.Id is not null && row is null) return (false, "This link no longer exists.");
        if (await _db.GitScanLinks.AnyAsync(l => l.Target == target && (row == null || l.Id != row.Id)))
            return (false, "This add-on is already linked; change the existing link.");
        var isNew = row is null;
        if (row is null)
        {
            row = new GitScanLink { CreatedAt = DateTime.UtcNow, CreatedBy = actor };
            _db.GitScanLinks.Add(row);
        }
        else { row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = actor; }
        row.Target = target; row.Repo = repo; row.Path = path; row.TagPattern = tag; row.Workflow = wf; row.AutoRelease = auto;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, isNew ? "gitscan.link.created" : "gitscan.link.changed", target,
            $"{repo}{(path.Length > 0 ? "/" + path : "")}; tags {tag}; {wf}; automatic approval {auto}");
        return (true, isNew ? "Link saved." : "Changes saved.");
    }

    public async Task<bool> DeleteLinkAsync(int id, string actor)
    {
        var row = await _db.GitScanLinks.FirstOrDefaultAsync(l => l.Id == id);
        if (row is null) return false;
        _db.GitScanLinks.Remove(row);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "gitscan.link.removed", row.Target, row.Repo);
        return true;
    }

    // ---- webhook --------------------------------------------------------------------------------------------

    public record WebhookResult(int Status, string Result);
    private static long s_lastRefusedTicks;

    /// <summary>
    /// One delivery from the git server. Signature first (nothing else is read before), every delivery id once.
    /// Step 1 records the delivery; importing versions follows in step 2.
    /// </summary>
    /// <summary>Cheap check before the body is read: connection on and a webhook secret stored.</summary>
    public async Task<bool> AcceptsWebhooksAsync() =>
        await _config.EnabledAsync() && (await _config.GetAsync(GitScanConfig.WebhookSecretKey)).Length > 0;

    public async Task<WebhookResult> ReceiveAsync(IHeaderDictionary headers, byte[] body)
    {
        var g = await _config.GitAsync();
        var secret = await _config.SecretAsync(GitScanConfig.WebhookSecretKey);
        if (!g.Enabled || string.IsNullOrEmpty(secret)) return new(404, "off");
        var provider = GitProviders.Find(g.Provider)!;
        if (!provider.VerifyWebhook(headers, body, secret, out var evt, out var delivery))
        {
            // at most one log entry a minute: unsigned requests must not flood the log (or the data share)
            var last = Interlocked.Read(ref s_lastRefusedTicks);
            if (DateTime.UtcNow.Ticks - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref s_lastRefusedTicks, DateTime.UtcNow.Ticks, last) == last)
                await LogEventAsync("webhook-refused", evt, delivery, "", "refused", "signature missing or wrong");
            return new(401, "refused");
        }
        // every delivery once: by its id, and by the body (the id is not signed: a captured body could come again under a new id)
        if (delivery.Length == 0) return new(400, "delivery id missing");
        var bodyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
        if (await _db.GitScanEvents.AnyAsync(e => e.Source == "webhook" && (e.DeliveryId == delivery || e.BodyHash == bodyHash)))
            return new(200, "duplicate");
        var repo = "";
        var detail = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("repository", out var r) && r.ValueKind == JsonValueKind.Object &&
                r.TryGetProperty("full_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                repo = fn.GetString() ?? "";
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                detail = "action " + a.GetString();
        }
        catch (JsonException) { detail = "body is not JSON"; }
        if (!RepoPattern.IsMatch(repo)) repo = "";
        var linked = repo.Length > 0 && await _db.GitScanLinks.AnyAsync(l => l.Repo.ToLower() == repo.ToLower());
        var result = evt == "ping" ? "accepted" : linked ? "recorded" : "ignored";
        if (!linked && evt != "ping" && repo.Length > 0) detail = (detail.Length > 0 ? detail + "; " : "") + "repository not linked";
        try { await LogEventAsync("webhook", evt, delivery, repo, result, detail, bodyHash); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); return new(200, "duplicate"); }   // the same delivery at the same moment
        await _config.SetAsync(GitScanConfig.LastWebhookKey, DateTime.UtcNow.ToString("O"));
        return new(evt == "ping" ? 200 : 202, result);
    }

    public async Task LogEventAsync(string source, string evt, string delivery, string repo, string result, string detail, string bodyHash = "")
    {
        _db.GitScanEvents.Add(new GitScanEvent
        {
            At = DateTime.UtcNow, Source = source, Event = Clip(evt, 60), DeliveryId = Clip(delivery, 80), Repo = Clip(repo, 200),
            Result = Clip(result, 20), Detail = Clip(detail, 600), BodyHash = bodyHash,
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Keeps the newest <see cref="MaxEvents"/> entries and nothing older than 180 days (background worker).</summary>
    public async Task<int> PruneAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        var removed = await _db.GitScanEvents.Where(e => e.At < cutoff).ExecuteDeleteAsync();
        var keepFrom = await _db.GitScanEvents.OrderByDescending(e => e.Id).Skip(MaxEvents).Select(e => (int?)e.Id).FirstOrDefaultAsync();
        if (keepFrom is { } k) removed += await _db.GitScanEvents.Where(e => e.Id <= k).ExecuteDeleteAsync();
        return removed;
    }

    /// <summary>A new webhook secret (shown once on the page, stored encrypted).</summary>
    public async Task<string> NewWebhookSecretAsync(string actor)
    {
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await _config.SetSecretAsync(GitScanConfig.WebhookSecretKey, secret);
        await _config.SetAsync(GitScanConfig.WebhookSecretAtKey, DateTime.UtcNow.ToString("O"));
        await _audit.LogAsync(actor, "gitscan.webhook.secret", "git", "new secret created (not logged)");
        return secret;
    }

    private static string Clip(string s, int max) =>
        new string((s ?? "").Select(ch => char.IsControl(ch) ? ' ' : ch).Take(max).ToArray());
}

/// <summary>Hourly: review reminders of add-ons out of service, pruning of the git event log.</summary>
public class GitScanWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GitScanWorker> _log;

    public GitScanWorker(IServiceScopeFactory scopes, ILogger<GitScanWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SuspensionService>().RemindAsync();
                await scope.ServiceProvider.GetRequiredService<GitScanService>().PruneAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning("Git & Veracode background run failed: {Error}", LogSafe.Of(e.Message));
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (TaskCanceledException) { return; }
        }
    }
}

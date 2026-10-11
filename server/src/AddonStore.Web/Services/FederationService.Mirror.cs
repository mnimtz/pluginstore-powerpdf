using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Stages E4 and E5 of docs/concepts/master-child.md (S1.24.0). Nothing of it runs unless a server is a paired UAT:
///
///   E4  The UAT takes over production's released state (public add-ons, newest live and newer beta version):
///       on demand, or once a day when the UAT admin switches it on (off by default). A version the UAT has
///       already, or older than one it tests, is left alone. Mirrored versions are released in the UAT with
///       origin "production", without mails, and are never handed back.
///   E5  A UAT admin hands a delivery template to production, without any customer assignment. Production
///       creates it (owner: the admin who added the UAT) or updates the one it received before, unless
///       production has assigned that one to customers already: then production's admins decide.
/// </summary>
public partial class FederationService
{
    public const int MaxMirrorPerRun = 25;
    private static readonly SemaphoreSlim MirrorGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (DateTime Start, int Count)> MirrorHits = new();

    private static bool MirrorLimited(string childId, int max)
    {
        var now = DateTime.UtcNow;
        var v = MirrorHits.AddOrUpdate(childId, _ => (now, 1), (_, o) => now - o.Start > TimeSpan.FromHours(1) ? (now, 1) : (o.Start, o.Count + 1));
        return v.Count > max;
    }

    // ---- production side ---------------------------------------------------------------------

    private async Task<List<PackageVersion>> MirrorSetAsync()
    {
        var cmp = new SemVerComparer();
        var versions = await _db.PackageVersions.AsNoTracking()
            .Where(v => (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta) && v.BlockedAt == null && v.PackageId != SubmissionService.ClientPackageId
                        && _db.Packages.Any(p => p.Id == v.PackageId && p.Visibility != "private" && p.BlockedAt == null))
            .ToListAsync();
        var set = new List<PackageVersion>();
        foreach (var g in versions.GroupBy(v => v.PackageId))
        {
            var live = g.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var beta = g.Where(v => v.Status == VersionStatus.Beta).OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            if (live is not null) set.Add(live);
            if (beta is not null && (live is null || cmp.Compare(beta.Version, live.Version) > 0)) set.Add(beta);
        }
        return set;
    }

    /// <summary>E4: production's released public add-ons for a paired UAT (no private deliveries, no customer data).</summary>
    public async Task<FedResult> MirrorCatalogAsync(FederationChild child)
    {
        if (MirrorLimited(child.Id + "|catalog", 60)) return Err("RATE_LIMITED", "More than 60 catalog requests from this UAT within an hour.", 429);
        var set = await MirrorSetAsync();
        var ids = set.Select(v => v.PackageId).Distinct().ToList();
        var pkgs = await _db.Packages.Include(p => p.Versions).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var items = set.Take(500).Select(v => new
        {
            packageId = v.PackageId, version = v.Version, status = v.Status == VersionStatus.Live ? "live" : "beta",
            sha256 = v.Sha256, size = v.SizeBytes, hasSource = !string.IsNullOrEmpty(v.SourcePath),
            catalog = pkgs.TryGetValue(v.PackageId, out var p) ? CatalogOf(p) : null,
        });
        return FedResult.Done(new { items });
    }

    /// <summary>E4: the upload package (.ppak and source ZIP) of one mirrored version as a temp file, or an error.</summary>
    public async Task<(string? Path, FedResult? Error)> MirrorPackageAsync(FederationChild child, string id, string version)
    {
        if (MirrorLimited(child.Id + "|package", 200)) return (null, Err("RATE_LIMITED", "More than 200 package downloads from this UAT within an hour.", 429));
        var v = (await MirrorSetAsync()).FirstOrDefault(x => x.PackageId == id && x.Version == version);
        if (v is null) return (null, Err("FED_NOT_MIRRORED", "This version is no released public version of production.", 404));
        var root = _sp.GetRequiredService<SubmissionService>().StorageRoot;
        var ppak = Path.Combine(root, v.FilePath);
        if (!File.Exists(ppak)) return (null, Err("FED_FILE_MISSING", "The package file is missing on production.", 500));
        var src = string.IsNullOrEmpty(v.SourcePath) ? null : Path.Combine(root, v.SourcePath);
        var tmp = Path.Combine(Path.GetTempPath(), "uat-mirror-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(ppak, $"{v.PackageId}-{v.Version}.ppak", CompressionLevel.NoCompression);
            if (src is not null && File.Exists(src)) zip.CreateEntryFromFile(src, $"{v.PackageId}-{v.Version}-source.zip", CompressionLevel.NoCompression);
        }
        return (tmp, null);
    }

    /// <summary>E5: a delivery template from a paired UAT, without customers.</summary>
    public async Task<FedResult> ReceiveTemplateAsync(FederationChild child, JsonElement? body)
    {
        if (body is not { } b) return Err("FED_BODY_INVALID", "Send the template as a JSON object.");
        if (MirrorLimited(child.Id + "|template", 60)) return Err("RATE_LIMITED", "More than 60 templates from this UAT within an hour.", 429);
        string S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var uatId = b.TryGetProperty("templateId", out var tid) && tid.TryGetInt32(out var n) ? n : 0;
        var name = S("name").Trim();
        var description = S("description");
        if (uatId <= 0 || TemplateService.CheckName(name, description.Length == 0 ? null : description) is not null)
            return Err("FED_TEMPLATE_INVALID", "templateId, a name (1 to 80 characters) and a description up to 1000 characters are needed.");
        var restrict = b.TryGetProperty("restrictCatalog", out var rc) && rc.ValueKind == JsonValueKind.True;
        var users = _sp.GetRequiredService<UserManager<AppUser>>();
        var owner = child.CreatedById is { Length: > 0 } aid && await users.FindByIdAsync(aid) is { Status: UserStatus.Active } a && await users.IsInRoleAsync(a, "Admin") ? a : null;
        if (owner is null) return Err("FED_TEMPLATE_INVALID", "The admin who added this UAT is no longer an active admin; remove the UAT and add it again.", 409);

        var customers = _sp.GetRequiredService<CustomerService>();
        var items = new List<TemplateService.ItemInput>();
        var skipped = new List<string>();
        var reset = new List<string>();
        if (b.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray().Take(TemplateService.MaxItems))
            {
                var pid = it.TryGetProperty("packageId", out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
                var ver = it.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString() : null;
                if (pid.Length == 0 || pid == SubmissionService.ClientPackageId || items.Any(x => x.PackageId == pid)) continue;
                if (!await _db.Packages.AnyAsync(x => x.Id == pid)) { skipped.Add(pid); continue; }
                // a version production has not approved: the template takes the newest approved one there
                if (!string.IsNullOrWhiteSpace(ver) && !(await customers.DeliverableVersionsAsync(pid)).Any(x => x.Version == ver.Trim()))
                {
                    reset.Add($"{pid} {ver}");
                    ver = null;
                }
                items.Add(new TemplateService.ItemInput(pid, string.IsNullOrWhiteSpace(ver) ? null : ver.Trim()));
            }

        var templates = _sp.GetRequiredService<TemplateService>();
        var existing = await _db.DeliveryTemplates.FirstOrDefaultAsync(t => t.OriginChildId == child.Id && t.OriginTemplateId == uatId);
        string state;
        if (existing is not null)
        {
            if (await _db.CustomerTemplates.AnyAsync(c => c.TemplateId == existing.Id))
                return Err("FED_TEMPLATE_IN_USE", "Production has assigned this template to customers; its admins change it there.", 409);
            await templates.UpdateAsync(existing, name, description, restrict, items, owner);
            state = "updated";
        }
        else
        {
            existing = await templates.CreateAsync(name, description, restrict, items, owner);
            existing.OriginChildId = child.Id;
            existing.OriginTemplateId = uatId;
            await _db.SaveChangesAsync();
            state = "created";
        }
        await _audit.LogAsync("UAT " + child.Name, "federation.template." + state, $"template {existing.Id}",
                              $"{name}; {items.Count} add-on(s); skipped {skipped.Count}; version reset {reset.Count}");
        return FedResult.Done(new { state, templateId = existing.Id, items = items.Count, skipped, versionReset = reset });
    }

    // ---- UAT side --------------------------------------------------------------------------------

    private async Task<(string Url, string Id, ECDsa Key)?> PairingAsync()
    {
        var url = await _settings.GetAsync(MasterUrlKey);
        var id = await _settings.GetAsync(PairingIdKey);
        if (!IsChild || url.Length == 0 || id.Length == 0) return null;
        var key = await OwnKeyAsync(create: false);
        return key is null ? null : (url, id, key);
    }

    /// <summary>A signed GET whose answer is a file: streamed to a temp file, the signature checked over its SHA-256.</summary>
    private async Task<(string? Path, FedResult? Error)> DownloadSignedAsync(string url, string pairingId, ECDsa key, long maxBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var uri = new Uri(url);
        req.Headers.Add("X-Federation-Id", pairingId);
        req.Headers.Add("X-Federation-Timestamp", ts);
        req.Headers.Add("X-Federation-Nonce", nonce);
        req.Headers.Add("X-Federation-Signature", Sign(key, RequestText(pairingId, "GET", uri.AbsolutePath + uri.Query, ts, nonce, Sha256Hex(Array.Empty<byte>()))));
        HttpResponseMessage resp;
        try { resp = await _http.CreateClient(UploadHttpName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (null, Err("FED_MASTER_UNREACHABLE", "The production server cannot be reached: " + e.GetType().Name + ".", 502));
        }
        using (resp)
        {
            if ((int)resp.StatusCode != 200)
                return (null, Err("FED_MASTER_UNREACHABLE", $"The production server refused the download (HTTP {(int)resp.StatusCode}).", 502));
            var tmp = Path.Combine(Path.GetTempPath(), "uat-mirror-in-" + Guid.NewGuid().ToString("N") + ".zip");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var file = File.Create(tmp))
            await using (var st = await resp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[81920];
                int n;
                while ((n = await st.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (total > maxBytes) break;
                    sha.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n));
                }
            }
            if (total > maxBytes) { TryDelete(tmp); return (null, Err("FED_TOO_LARGE", "The download is larger than 220 MB.", 502)); }
            using var mk = ImportPublicKey(await _settings.GetAsync(MasterPublicKeyKey));
            var sig = resp.Headers.TryGetValues("X-Federation-Signature", out var v) ? v.FirstOrDefault() ?? "" : "";
            if (mk is null || !Verify(mk, ResponseText(nonce, 200, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()), sig))
            {
                TryDelete(tmp);
                return (null, Err("FED_ANSWER_UNSIGNED", "The download is not signed with the federation key of production; pair again.", 502));
            }
            return (tmp, null);
        }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public record MirrorItem(string PackageId, string Version, string Result, string Detail);

    /// <summary>
    /// E4: takes over production's released public versions that the UAT is missing (at most 25 per run).
    /// Skips a version the UAT has, and an add-on the UAT already tests in a newer version.
    /// </summary>
    public async Task<FedResult> MirrorAsync(AppUser actor)
    {
        var pairing = await PairingAsync();
        if (pairing is not { } pr) return Err("FED_NOT_PAIRED", "This UAT is not paired with a production server.", 409);
        using var key = pr.Key;
        if (!await MirrorGate.WaitAsync(0)) return Err("FED_MIRROR_RUNNING", "The UAT is mirroring production already.", 409);
        try
        {
            var (st, j, _, e) = await SendAsync(HttpMethod.Get, pr.Url + "/api/federation/catalog", Array.Empty<byte>(), (pr.Id, key), maxResponse: 8 * 1024 * 1024);
            if (e is not null) return e;
            if (j is not { } d || !d.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return MasterError(st, j);
            var cmp = new SemVerComparer();
            var users = _sp.GetRequiredService<UserManager<AppUser>>();
            var submissions = _sp.GetRequiredService<SubmissionService>();
            var sources = _sp.GetRequiredService<SourceService>();
            var meta = _sp.GetRequiredService<PackageMetaService>();
            var result = new List<MirrorItem>();
            var done = 0;
            foreach (var it in d.GetProperty("data").GetProperty("items").EnumerateArray())
            {
                string S(string k) => it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var id = S("packageId");
                var version = S("version");
                var status = S("status");
                if (id.Length == 0 || version.Length == 0 || status is not ("live" or "beta")) continue;
                var here = await _db.PackageVersions.AsNoTracking().Where(x => x.PackageId == id).Select(x => new { x.Version, x.Status }).ToListAsync();
                if (here.Any(x => x.Version == version)) { result.Add(new(id, version, "present", "")); continue; }
                if (here.Any(x => x.Status is not (VersionStatus.Rejected or VersionStatus.Withdrawn) && cmp.Compare(x.Version, version) > 0))
                { result.Add(new(id, version, "newer-in-uat", "")); continue; }
                if (done >= MaxMirrorPerRun) { result.Add(new(id, version, "later", "")); continue; }
                done++;

                var (tmp, de) = await DownloadSignedAsync($"{pr.Url}/api/federation/catalog/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/package",
                                                          pr.Id, key, MaxUploadBytes);
                if (de is not null) { result.Add(new(id, version, "failed", de.Code)); continue; }
                try
                {
                    UploadBundle.Result? bundle;
                    try { bundle = UploadBundle.TryUnpack(tmp!); }
                    catch (InvalidDataException ex) { result.Add(new(id, version, "failed", ex.Message)); continue; }
                    if (bundle is null) { result.Add(new(id, version, "failed", "no upload package")); continue; }
                    using (bundle)
                    {
                        string FileHash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
                        if (FileHash(bundle.PpakPath) != S("sha256").ToLowerInvariant()) { result.Add(new(id, version, "failed", "SHA-256 differs")); continue; }
                        var isNew = !await _db.Packages.AnyAsync(p => p.Id == id);
                        var sub = await submissions.SubmitAsync(bundle.PpakPath, actor, "mirror", notify: false);
                        if (sub.Version is not { } nv)
                        {
                            result.Add(new(id, version, "failed", sub.ErrorCode ?? sub.Report.Findings.FirstOrDefault(f => f.Severity == "error")?.Code ?? "refused"));
                            continue;
                        }
                        if (bundle.SourcePath is not null) await sources.UploadAsync(nv, bundle.SourcePath, actor, true);
                        var tracked = await _db.PackageVersions.FirstAsync(x => x.Id == nv.Id);
                        tracked.Status = status == "live" ? VersionStatus.Live : VersionStatus.Beta;
                        tracked.ReviewedById = actor.Id;
                        tracked.ReviewedAt = DateTime.UtcNow;
                        tracked.ReviewComment = "Released on production; mirrored from " + pr.Url;
                        tracked.OriginNote = "production";
                        await _db.SaveChangesAsync();
                        // a new add-on takes production's catalog entry; an existing one keeps what the UAT tests
                        if (isNew && it.TryGetProperty("catalog", out var cat) && cat.ValueKind == JsonValueKind.Object)
                            await ApplyMirroredCatalogAsync(id, cat, actor, meta);
                        result.Add(new(id, version, "mirrored", status));
                    }
                }
                finally { TryDelete(tmp!); }
            }
            var mirrored = result.Count(r => r.Result == "mirrored");
            var failed = result.Count(r => r.Result == "failed");
            await _settings.SetAsync(LastMirrorKey, $"{DateTime.UtcNow:o}|{mirrored}|{result.Count - mirrored - failed}|{failed}");
            await _audit.LogAsync(actor.DisplayName, "federation.mirror", pr.Url, $"mirrored {mirrored}, failed {failed}, unchanged {result.Count - mirrored - failed}");
            return FedResult.Done(new { mirrored, failed, items = result.Select(r => new { packageId = r.PackageId, version = r.Version, result = r.Result, detail = r.Detail }) });
        }
        finally { MirrorGate.Release(); }
    }

    private async Task ApplyMirroredCatalogAsync(string id, JsonElement cat, AppUser actor, PackageMetaService meta)
    {
        var pkg = await _db.Packages.FirstOrDefaultAsync(p => p.Id == id);
        if (pkg is null) return;
        Dictionary<string, string>? Map(string k) => cat.TryGetProperty(k, out var el) && el.ValueKind == JsonValueKind.Object
            ? el.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString() ?? "") : null;
        string? Str(string k) => cat.TryGetProperty(k, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        // one field at a time: a field that does not pass the catalog checks keeps the manifest value, the others still apply
        var changes = new List<MetaChange>
        {
            new() { SetName = true, Name = Map("name") },
            new() { SetDescription = true, Description = Map("description") },
            new() { SetAuthor = true, Author = Str("author") },
            new() { SetContact = true, ContactEmail = Str("contactEmail") },
            new() { SetCategory = true, Category = Str("category") },
        };
        if (cat.TryGetProperty("providers", out var pv) && pv.ValueKind == JsonValueKind.Array)
            changes.Add(new MetaChange
            {
                SetProviders = true,
                Providers = pv.EnumerateArray().Select(x => new AddonProvider(x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                                                              x.TryGetProperty("website", out var w) ? w.GetString() ?? "" : "")).ToList(),
            });
        foreach (var change in changes)
        {
            var issues = await meta.ApplyAsync(pkg, actor, change);
            if (issues.Any(i => i.Severity == "error"))
                await _audit.LogAsync(actor.DisplayName, "federation.mirror.catalog", id, string.Join(", ", issues.Where(i => i.Severity == "error").Select(i => i.Code)));
        }
    }

    /// <summary>E5: hands one delivery template to production (no customers).</summary>
    public async Task<FedResult> HandOverTemplateAsync(int templateId, AppUser actor)
    {
        var pairing = await PairingAsync();
        if (pairing is not { } pr) return Err("FED_NOT_PAIRED", "This UAT is not paired with a production server.", 409);
        using var key = pr.Key;
        var users = _sp.GetRequiredService<UserManager<AppUser>>();
        var t = await _db.DeliveryTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        if (t is null) return Err("TEMPLATE_NOT_FOUND", "No template with this id.", 404);
        if (t.OwnerId != actor.Id && !await users.IsInRoleAsync(actor, "Admin"))
            return Err("NOT_OWNER", "Only the creator of the template or an admin hands it to production.", 403);
        var items = await _db.DeliveryTemplateItems.Where(i => i.TemplateId == t.Id).Select(i => new { packageId = i.PackageId, version = i.Version }).ToListAsync();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { templateId = t.Id, name = t.Name, description = t.Description ?? "", restrictCatalog = t.RestrictCatalog, items });
        var (st, j, _, e) = await SendAsync(HttpMethod.Post, pr.Url + "/api/federation/templates", body, (pr.Id, key));
        if (e is not null) return e;
        if (j is not { } d || !d.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return MasterError(st, j);
        t.HandedOverAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor.DisplayName, "federation.template.handed-over", $"template {t.Id}", t.Name);
        return FedResult.Done(d.GetProperty("data"));
    }
}

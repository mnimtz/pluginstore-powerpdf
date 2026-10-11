using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Stage E2 of docs/concepts/master-child.md (S1.22.0): a UAT hands one version to its production server.
///
///   1. The UAT announces it, signed: POST /api/federation/promotions {packageId, version, sha256, sourceSha256,
///      note, uploaderEmail, uploaderName, handedOverBy, review} and receives an intake id.
///   2. It sends the upload package (the exact .ppak bytes it tested plus the source ZIP), signed:
///      PUT /api/federation/promotions/{id}/package.
///   3. Production checks the hashes, then runs the normal submission pipeline (all automatic checks, source
///      policy) and stores the version as submitted with its UAT origin. A UAT approval is never an approval
///      here: reviewers of production decide, and the UAT uploader cannot approve it (four-eyes across both).
/// </summary>
public partial class FederationService
{
    private static readonly Regex PackageIdPattern = new(@"^[a-z0-9]+(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[0-9A-Za-z.+-]{1,40}$", RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, (DateTime Start, int Count)> HandoverHits = new();
    public const int MaxHandoversPerHour = 30;
    public const long MaxUploadBytes = 220L * 1024 * 1024;   // the request limit of every upload

    private static bool HandoverLimited(string childId)
    {
        var now = DateTime.UtcNow;
        var v = HandoverHits.AddOrUpdate(childId, _ => (now, 1), (_, o) => now - o.Start > TimeSpan.FromHours(1) ? (now, 1) : (o.Start, o.Count + 1));
        return v.Count > MaxHandoversPerHour;
    }

    private static string Cap(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();

    // ---- production (master) side ---------------------------------------------------------

    /// <summary>Step 1: a paired UAT announces a version.</summary>
    public async Task<FedResult> OpenIntakeAsync(FederationChild child, JsonElement? body)
    {
        if (body is not { } b) return Err("FED_BODY_INVALID", "Send the handover as a JSON object.");
        string S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var id = S("packageId").Trim();
        var version = S("version").Trim();
        var sha = S("sha256").Trim().ToLowerInvariant();
        var srcSha = S("sourceSha256").Trim().ToLowerInvariant();
        static bool Hex64(string h) => h.Length == 64 && h.All(Uri.IsHexDigit);
        if (id.Length > 100 || !PackageIdPattern.IsMatch(id) || !VersionPattern.IsMatch(version) || !Hex64(sha) || !(srcSha.Length == 0 || Hex64(srcSha)))
            return Err("FED_HANDOVER_INVALID", "packageId, version and sha256 (64 hex characters) are required; sourceSha256 is 64 hex characters or empty.");
        if (HandoverLimited(child.Id))
            return Err("RATE_LIMITED", $"More than {MaxHandoversPerHour} handovers from this UAT within an hour.", 429);
        var review = b.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetRawText() : "";
        if (review.Length > 4000) review = "";
        // an announcement that never got its package is of no use after an hour
        var old = DateTime.UtcNow.AddHours(-1);
        await _db.FederationIntakes.Where(x => x.State == "waiting" && x.CreatedAt < old).ExecuteDeleteAsync();
        var row = new FederationIntake
        {
            ChildId = child.Id, PackageId = id, Version = version, Sha256 = sha, SourceSha256 = srcSha,
            Note = Cap(S("note"), 1000), UploaderEmail = Cap(S("uploaderEmail"), 254), UploaderName = Cap(S("uploaderName"), 100),
            HandedOverBy = Cap(S("handedOverBy"), 100), ReviewJson = review,
        };
        _db.FederationIntakes.Add(row);
        await _db.SaveChangesAsync();
        return FedResult.Done(new { intakeId = row.Id, maxBytes = MaxUploadBytes });
    }

    public record IntakeAnswer(string IntakeId, string State, string Message, string? VersionStatus, List<Finding> Findings);

    private async Task<FedResult> RejectIntakeAsync(FederationIntake row, FederationChild child, string message, List<Finding>? findings = null)
    {
        row.State = "rejected";
        row.Message = message;
        row.FindingsJson = JsonSerializer.Serialize(findings ?? new List<Finding>());
        row.DoneAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("UAT " + child.Name, "federation.handover.rejected", $"{row.PackageId} {row.Version}", message);
        return FedResult.Done(new IntakeAnswer(row.Id, "rejected", message, null, findings ?? new List<Finding>()));
    }

    /// <summary>Step 2: the upload package of an announced version, already on disk (hash checked by the caller's signature).</summary>
    public async Task<FedResult> ReceivePackageAsync(FederationChild child, string intakeId, string uploadZip)
    {
        var row = await _db.FederationIntakes.FirstOrDefaultAsync(x => x.Id == intakeId && x.ChildId == child.Id);
        if (row is null) return Err("FED_HANDOVER_NOT_FOUND", "No announced handover with this id (older than an hour, or of another UAT).", 404);
        if (row.State != "waiting") return Err("FED_HANDOVER_DONE", "This handover was answered already.", 409);

        UploadBundle.Result? bundle;
        try { bundle = UploadBundle.TryUnpack(uploadZip); }
        catch (InvalidDataException e) { return await RejectIntakeAsync(row, child, e.Message); }
        if (bundle is null) return await RejectIntakeAsync(row, child, "The body is no upload package (.ppak and source ZIP in one ZIP).");
        using (bundle)
        {
            // the same bytes the UAT tested
            string FileHash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
            if (FileHash(bundle.PpakPath) != row.Sha256)
                return await RejectIntakeAsync(row, child, "The .ppak differs from the announced SHA-256.");
            if (row.SourceSha256.Length > 0 && (bundle.SourcePath is null || FileHash(bundle.SourcePath) != row.SourceSha256))
                return await RejectIntakeAsync(row, child, "The source ZIP differs from the announced SHA-256.");

            var users = _sp.GetRequiredService<UserManager<AppUser>>();
            var submissions = _sp.GetRequiredService<SubmissionService>();
            var sources = _sp.GetRequiredService<SourceService>();

            // uploader on production: the account with the UAT uploader's e-mail, else the admin who added this UAT
            AppUser? uploader = null;
            if (row.UploaderEmail.Contains('@'))
                uploader = await users.FindByEmailAsync(row.UploaderEmail) is { Status: UserStatus.Active } u ? u : null;
            if (uploader is null && child.CreatedById is { Length: > 0 } adminId)
                uploader = await users.FindByIdAsync(adminId) is { Status: UserStatus.Active } a && await users.IsInRoleAsync(a, "Admin") ? a : null;
            if (uploader is null)
                return await RejectIntakeAsync(row, child, "Production has no account with the uploader's e-mail address and the admin who added this UAT is no longer active.");

            // the source policy of production, as for every upload (POST /api/packages)
            if (await sources.RequiredAtSubmitAsync(bundle.PpakPath))
            {
                if (bundle.SourcePath is null)
                    return await RejectIntakeAsync(row, child, "Production requires the source code of every version; the handover has none.");
                var srcReport = SourceService.CheckForSubmit(bundle.SourcePath);
                if (!srcReport.Passed)
                    return await RejectIntakeAsync(row, child, "The source code did not pass the checks of production.", srcReport.Findings);
            }

            var result = await submissions.SubmitAsync(bundle.PpakPath, uploader, "uat:" + child.Name);
            if (result.Version is not { } v)
                return await RejectIntakeAsync(row, child, result.ErrorCode is { } ec ? $"Refused by production ({ec})." : "The package did not pass the checks of production.",
                                               result.Report.Findings);
            if (v.PackageId != row.PackageId || v.Version != row.Version || v.Sha256 != row.Sha256)
            {
                // cannot happen with matching hashes; never keep a version that differs from the announcement
                v.Status = VersionStatus.Rejected;
                await _db.SaveChangesAsync();
                return await RejectIntakeAsync(row, child, "The package names another id or version than announced.");
            }
            if (bundle.SourcePath is not null)
                await sources.UploadAsync(v, bundle.SourcePath, uploader, await users.IsInRoleAsync(uploader, "Admin"));

            var tracked = await _db.PackageVersions.FirstAsync(x => x.Id == v.Id);
            tracked.OriginChildId = child.Id;
            tracked.OriginUat = child.Name;
            tracked.OriginNote = row.Note;
            tracked.OriginUploaderEmail = row.UploaderEmail;
            tracked.OriginUploader = row.UploaderName;
            tracked.OriginReviewJson = row.ReviewJson;
            tracked.OriginAt = DateTime.UtcNow;
            row.State = "accepted";
            row.Message = "Stored as a submitted version; production reviews it.";
            row.FindingsJson = JsonSerializer.Serialize(result.Report.Findings);
            row.VersionId = v.Id;
            row.DoneAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync("UAT " + child.Name, "federation.handover.received", $"{v.PackageId} {v.Version}",
                                  $"intake {row.Id}, uploader {uploader.DisplayName}, sha256 {v.Sha256}");
            return FedResult.Done(new IntakeAnswer(row.Id, "accepted", row.Message, "submitted", result.Report.Findings));
        }
    }

    /// <summary>"beta · Jane Doe · Oct 11, 2026: comment" from the review JSON the UAT sent (for the review page).</summary>
    public static string ReviewExcerpt(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var at = r.TryGetProperty("reviewedAt", out var a) && a.ValueKind == JsonValueKind.String && DateTime.TryParse(a.GetString(), out var t)
                ? t.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
            var parts = new[] { S("status"), S("reviewedBy"), at }.Where(x => x.Length > 0);
            var text = string.Join(" · ", parts);
            var c = S("comment");
            return Cap(c.Length > 0 ? text + ": " + c : text, 600);
        }
        catch (JsonException) { return ""; }
    }

    public async Task<List<FederationIntake>> IntakesAsync(int take = 50) =>
        await _db.FederationIntakes.OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync();

    // ---- UAT side --------------------------------------------------------------------------

    public async Task<List<FederationHandover>> HandoversAsync(string? packageId = null, int take = 50) =>
        await _db.FederationHandovers.Where(h => packageId == null || h.PackageId == packageId)
            .OrderByDescending(h => h.CreatedAt).Take(take).ToListAsync();

    /// <summary>
    /// Hands one approved version (beta or live) to production: its exact .ppak and source ZIP, the note, its uploader
    /// and its review here. Admins and the owner of the add-on may do it.
    /// </summary>
    public async Task<FedResult> HandOverAsync(int versionId, string? note, AppUser actor)
    {
        var url = await _settings.GetAsync(MasterUrlKey);
        var pairingId = await _settings.GetAsync(PairingIdKey);
        if (!IsChild || pairingId.Length == 0 || url.Length == 0) return Err("FED_NOT_PAIRED", "This UAT is not paired with a production server.", 409);
        var v = await _db.PackageVersions.Include(x => x.Package).FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null) return Err("VERSION_NOT_FOUND", "No version with this id.", 404);
        var users = _sp.GetRequiredService<UserManager<AppUser>>();
        if (!(v.Package?.OwnerId == actor.Id || await users.IsInRoleAsync(actor, "Admin")))
            return Err("NOT_OWNER", "Only the owner of the add-on or an admin hands a version to production.", 403);
        if (v.Status is not (VersionStatus.Beta or VersionStatus.Live) || v.BlockedAt is not null)
            return Err("FED_VERSION_NOT_APPROVED", "Only a version approved here (beta or live) and not blocked goes to production.", 409);
        if (await _db.FederationHandovers.AnyAsync(h => h.VersionId == v.Id && h.State == "accepted"))
            return Err("FED_HANDED_OVER", "This version was handed to production already.", 409);
        using var own = await OwnKeyAsync(create: false);
        if (own is null) return Err("FED_KEY_UNREADABLE", "The federation key of this UAT cannot be read (restored without its key ring); pair again.", 500);

        var submissions = _sp.GetRequiredService<SubmissionService>();
        var ppak = Path.Combine(submissions.StorageRoot, v.FilePath);
        var src = v.SourcePath is { Length: > 0 } sp ? Path.Combine(submissions.StorageRoot, sp) : null;
        if (!File.Exists(ppak)) return Err("FED_FILE_MISSING", "The package file of this version is missing on this server.", 500);
        if (src is not null && !File.Exists(src)) src = null;

        var uploader = v.SubmittedById is { Length: > 0 } uid ? await users.FindByIdAsync(uid) : null;
        var reviewer = v.ReviewedById is { Length: > 0 } rid ? await users.FindByIdAsync(rid) : null;
        string FileHash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
        var handover = new FederationHandover
        {
            VersionId = v.Id, PackageId = v.PackageId, Version = v.Version, Sha256 = FileHash(ppak),
            Note = Cap(note, 1000), CreatedBy = actor.DisplayName,
        };
        _db.FederationHandovers.Add(handover);
        await _db.SaveChangesAsync();

        var tmp = Path.Combine(Path.GetTempPath(), "uat-handover-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(ppak, $"{v.PackageId}-{v.Version}.ppak", CompressionLevel.NoCompression);
                if (src is not null) zip.CreateEntryFromFile(src, $"{v.PackageId}-{v.Version}-source.zip", CompressionLevel.NoCompression);
            }
            if (new FileInfo(tmp).Length > MaxUploadBytes)
                return await FailHandoverAsync(handover, Err("FED_TOO_LARGE", "Package and source together are larger than 220 MB.", 413));

            var announce = JsonSerializer.SerializeToUtf8Bytes(new
            {
                packageId = v.PackageId, version = v.Version, sha256 = handover.Sha256, sourceSha256 = src is null ? "" : FileHash(src),
                note = handover.Note, uploaderEmail = uploader?.Email ?? "", uploaderName = uploader?.DisplayName ?? v.SubmittedBy,
                handedOverBy = actor.DisplayName,
                review = new
                {
                    status = v.Status == VersionStatus.Live ? "live" : "beta", reviewedBy = reviewer?.DisplayName ?? "",
                    reviewedAt = v.ReviewedAt, comment = Cap(v.ReviewComment, 1000), server = await OwnBaseUrlAsync(),
                },
            });
            var (st1, j1, _, e1) = await SendAsync(HttpMethod.Post, url + "/api/federation/promotions", announce, (pairingId, own));
            if (e1 is not null) return await FailHandoverAsync(handover, e1);
            if (j1 is not { } a || !a.TryGetProperty("ok", out var ok1) || ok1.ValueKind != JsonValueKind.True)
                return await FailHandoverAsync(handover, MasterError(st1, j1));
            var intakeId = a.GetProperty("data").GetProperty("intakeId").GetString() ?? "";
            if (intakeId.Length is < 1 or > 64 || !intakeId.All(char.IsAsciiLetterOrDigit))
                return await FailHandoverAsync(handover, Err("FED_MASTER_UNREACHABLE", "The production server sent an invalid answer.", 502));
            handover.MasterIntakeId = intakeId;

            var (st2, j2, _, e2) = await SendAsync(HttpMethod.Put, url + "/api/federation/promotions/" + intakeId + "/package", Array.Empty<byte>(), (pairingId, own), tmp);
            if (e2 is not null) return await FailHandoverAsync(handover, e2);
            if (j2 is not { } d || !d.TryGetProperty("ok", out var ok2) || ok2.ValueKind != JsonValueKind.True)
                return await FailHandoverAsync(handover, MasterError(st2, j2));
            var data = d.GetProperty("data");
            var state = data.TryGetProperty("state", out var sv) ? sv.GetString() ?? "" : "";
            handover.State = state == "accepted" ? "accepted" : "rejected";
            handover.Message = data.TryGetProperty("message", out var mv) ? Cap(mv.GetString(), 1000) : "";
            handover.FindingsJson = data.TryGetProperty("findings", out var fv) && fv.ValueKind == JsonValueKind.Array && fv.GetRawText().Length < 200000 ? fv.GetRawText() : "[]";
            handover.DoneAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _audit.LogAsync(actor.DisplayName, "federation.handover." + handover.State, $"{v.PackageId} {v.Version}", $"to {url}, intake {intakeId}");
            return FedResult.Done(new { handoverId = handover.Id, state = handover.State, message = handover.Message,
                                        findings = JsonSerializer.Deserialize<JsonElement>(handover.FindingsJson) });
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    private async Task<string> OwnBaseUrlAsync() => (await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');

    private static FedResult MasterError(int status, JsonElement? json)
    {
        var c = json is { } k && k.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var ec) ? ec.GetString() ?? "" : "";
        var m = json is { } k2 && k2.TryGetProperty("error", out var e2) && e2.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "";
        if (status == 404 && c.Length == 0) return Err("FED_NOT_PAIRED", "The production server does not know this UAT any more; pair again.", 502);
        return Err(c.StartsWith("FED_") || c == "RATE_LIMITED" ? c : "FED_MASTER_UNREACHABLE",
                   "The production server refused the handover" + (m.Length > 0 ? ": " + Cap(m, 300) : $" (HTTP {status})."), 502);
    }

    private async Task<FedResult> FailHandoverAsync(FederationHandover h, FedResult error)
    {
        h.State = "failed";
        h.Code = error.Code;
        h.Message = error.Message;
        h.DoneAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(h.CreatedBy, "federation.handover.failed", $"{h.PackageId} {h.Version}", error.Code);
        return error;
    }
}

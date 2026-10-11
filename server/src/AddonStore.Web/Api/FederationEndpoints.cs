using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Api;

/// <summary>
/// UAT environments (S1.21.0, docs/concepts/master-child.md):
///   /api/federation/*  between servers: pairing with a one-time code, then signed requests of the UAT.
///                      Answers 404 while this server has no UAT and no open pairing code.
///   /api/admin/uat/*   for admins (Bearer token, role Admin): the same steps as the portal page /Admin/Uat.
/// Envelope like the rest of the API: {ok, data} or {ok:false, error:{code, message}}.
/// </summary>
public static class FederationEndpoints
{
    private static readonly ConcurrentDictionary<string, (DateTime Start, int Count)> Hits = new();
    private static bool Limited(HttpContext ctx, string kind, int max, TimeSpan window)
    {
        var now = DateTime.UtcNow;
        var key = kind + "|" + (GeoService.ClientIp(ctx)?.ToString() ?? "?");
        var v = Hits.AddOrUpdate(key, _ => (now, 1), (_, o) => now - o.Start > window ? (now, 1) : (o.Start, o.Count + 1));
        if (Hits.Count > 20000)
            foreach (var k in Hits.Where(x => now - x.Value.Start > TimeSpan.FromDays(1)).Select(x => x.Key).ToList()) Hits.TryRemove(k, out _);
        return v.Count > max;
    }

    private static IResult Ok(object? data = null) => Results.Json(new { ok = true, data });
    private static IResult Err(string code, string message, int status = 400) =>
        Results.Json(new { ok = false, error = new { code, message } }, statusCode: status);
    private static IResult Answer(FedResult r) => r.Ok ? Ok(r.Data) : Err(r.Code, r.Message, r.Status);

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, int max)
    {
        var buf = new byte[max];
        var n = 0;
        int r;
        while (n < max && (r = await ctx.Request.Body.ReadAsync(buf.AsMemory(n))) > 0) n += r;
        if (n == max && await ctx.Request.Body.ReadAsync(new byte[1]) > 0) return null;
        return buf.AsSpan(0, n).ToArray();
    }

    private static JsonElement? Json(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string S(JsonElement? b, string k) =>
        b is { } e && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<AppUser?> AdminAsync(HttpContext ctx, UserManager<AppUser> users)
    {
        var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !ctx.User.IsInRole("Admin")) return null;
        var user = await users.FindByIdAsync(id);
        return user is { Status: UserStatus.Active } ? user : null;
    }

    // the public address of this server (App.PublicBaseUrl wins over the request)
    private static string OwnUrl(HttpContext ctx)
    {
        var s = ctx.RequestServices.GetRequiredService<SettingsService>().GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl").GetAwaiter().GetResult().TrimEnd('/');
        return s.Length > 0 ? s : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    }

    /// <summary>The answer to a signed request, signed by this server (the UAT checks it on 200).</summary>
    private static async Task<IResult> SignedAsync(HttpContext ctx, FederationService svc, FedResult r)
    {
        if (!r.Ok) return Err(r.Code, r.Message, r.Status);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, data = r.Data }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        ctx.Response.Headers["X-Federation-Signature"] = await svc.SignResponseAsync(ctx.Request.Headers["X-Federation-Nonce"].ToString(), 200, bytes);
        return Results.Bytes(bytes, "application/json; charset=utf-8");
    }

    private static IResult NotAdmin() => Err("ADMIN_ONLY", "Only store admins manage UAT environments.", 403);

    public static void MapFederation(this WebApplication app)
    {
        // ---- between servers ---------------------------------------------------------------
        var fed = app.MapGroup("/api/federation");

        // A UAT pairs with a code from "Add UAT". Anonymous; the code is the proof.
        fed.MapPost("/pair", async (HttpContext ctx, FederationService svc) =>
        {
            if (!await svc.MasterActiveAsync()) return Results.NotFound();
            if (Limited(ctx, "fedpair", 20, TimeSpan.FromHours(1)))
                return Err("RATE_LIMITED", "Too many pairing attempts from this address; try again later.", 429);
            var body = await ReadBodyAsync(ctx, 16384);
            var j = body is null ? null : Json(body);
            if (j is null) return Err("FED_BODY_INVALID", "Send {\"code\": \"...\", \"publicKey\": \"...\", \"url\": \"...\"} as JSON (at most 16 KB).");
            return Answer(await svc.PairAsync(S(j, "code"), S(j, "publicKey"), S(j, "url"), GeoService.ClientIp(ctx)?.ToString() ?? "?"));
        });

        // Signed by the UAT; the answer is signed by this server. Checks pairing, clocks and keys.
        fed.MapGet("/ping", async (HttpContext ctx, FederationService svc) =>
        {
            if (!await svc.MasterActiveAsync()) return Results.NotFound();
            var body = await ReadBodyAsync(ctx, 1024) ?? Array.Empty<byte>();
            var (child, error) = await svc.VerifyRequestAsync(ctx.Request, body);
            if (error is not null) return Answer(error);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                data = new { pairingId = child!.Id, name = child.Name, status = "active", time = DateTime.UtcNow },
            });
            ctx.Response.Headers["X-Federation-Signature"] = await svc.SignResponseAsync(ctx.Request.Headers["X-Federation-Nonce"].ToString(), 200, bytes);
            return Results.Bytes(bytes, "application/json; charset=utf-8");
        });

        // Handover, step 1 (S1.22.0): a paired UAT announces one version; answer: the intake id.
        fed.MapPost("/promotions", async (HttpContext ctx, FederationService svc) =>
        {
            if (!await svc.MasterActiveAsync()) return Results.NotFound();
            var body = await ReadBodyAsync(ctx, 65536);
            if (body is null) return Err("FED_BODY_INVALID", "The announcement is larger than 64 KB.");
            var (child, error) = await svc.VerifyRequestAsync(ctx.Request, body);
            if (error is not null) return Answer(error);
            return await SignedAsync(ctx, svc, await svc.OpenIntakeAsync(child!, Json(body)));
        });

        // Handover, step 2: the upload package (.ppak and source ZIP) of an announced version, signed over its SHA-256.
        // Cheap checks first (known, unblocked UAT, clock, announced handover), then the body is streamed to a file.
        fed.MapPut("/promotions/{id}/package", async (string id, HttpContext ctx, FederationService svc, AppDbContext db) =>
        {
            if (!await svc.MasterActiveAsync()) return Results.NotFound();
            var childId = ctx.Request.Headers["X-Federation-Id"].ToString();
            if (!long.TryParse(ctx.Request.Headers["X-Federation-Timestamp"].ToString(), out var unix) ||
                (DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, 0, 253402300799)).UtcDateTime).Duration() > FederationService.MaxClockSkew)
                return Err("FED_TIMESTAMP", "The request is older than 5 minutes or the clocks differ; check the time of both servers.", 401);
            if (id.Length > 64 || childId.Length is < 1 or > 40 ||
                !await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.FederationIntakes,
                    x => x.Id == id && x.ChildId == childId && x.State == "waiting"))
                return Err("FED_HANDOVER_NOT_FOUND", "No announced handover with this id (older than an hour, or of another UAT).", 404);
            if (ctx.Request.ContentLength is > FederationService.MaxUploadBytes)
                return Err("FED_TOO_LARGE", "Package and source together are larger than 220 MB.", 413);
            var tmp = Path.Combine(Path.GetTempPath(), "uat-intake-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                string hash;
                await using (var file = File.Create(tmp))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await ctx.Request.Body.ReadAsync(buf)) > 0)
                    {
                        total += n;
                        if (total > FederationService.MaxUploadBytes) return Err("FED_TOO_LARGE", "Package and source together are larger than 220 MB.", 413);
                        sha.AppendData(buf, 0, n);
                        await file.WriteAsync(buf.AsMemory(0, n));
                    }
                    hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }
                var (child, error) = await svc.VerifyRequestAsync(ctx.Request, hash);
                if (error is not null) return Answer(error);
                return await SignedAsync(ctx, svc, await svc.ReceivePackageAsync(child!, id, tmp));
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        });

        // ---- owners and admins of a UAT: hand a version to production (S1.22.0) -------------------
        var pkgs = app.MapGroup("/api/packages").RequireAuthorization("BearerOnly");
        pkgs.MapPost("/{id}/{version}/handover", async (string id, string version, HttpContext ctx, UserManager<AppUser> users,
                                                       FederationService svc, AppDbContext db) =>
        {
            var uid = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = uid is null ? null : await users.FindByIdAsync(uid);
            if (user is not { Status: UserStatus.Active }) return Err("UNAUTHENTICATED", "Send a valid API token.", 401);
            var v = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.PackageVersions,
                        x => x.PackageId == id && x.Version == version);
            if (v is null) return Err("VERSION_NOT_FOUND", $"No version {version} of '{id}'.", 404);
            var j = Json(await ReadBodyAsync(ctx, 8192) ?? Array.Empty<byte>());
            return Answer(await svc.HandOverAsync(v.Id, S(j, "note"), user));
        });

        // ---- admins ------------------------------------------------------------------------
        var adm = app.MapGroup("/api/admin/uat").RequireAuthorization("BearerOnly");

        adm.MapGet("", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            if (await AdminAsync(ctx, users) is null) return NotAdmin();
            var st = await svc.ChildStateAsync();
            var children = (await svc.ChildrenAsync()).Select(c => new
            {
                id = c.Id, name = c.Name, keyId = FederationService.KeyIdOf(c.PublicKey), url = c.ChildUrl,
                createdAt = c.CreatedAt, lastSeenAt = c.LastSeenAt, blocked = c.BlockedAt is not null,
            });
            var codes = (await svc.OpenCodesAsync()).Select(c => new { id = c.Id, name = c.Name, createdAt = c.CreatedAt, expiresAt = c.ExpiresAt });
            var intakes = st.IsChild ? null : (await svc.IntakesAsync()).Select(x => new
            {
                id = x.Id, uatId = x.ChildId, packageId = x.PackageId, version = x.Version, state = x.State, message = x.Message,
                versionId = x.VersionId, createdAt = x.CreatedAt, doneAt = x.DoneAt,
            });
            var handovers = !st.IsChild ? null : (await svc.HandoversAsync()).Select(h => new
            {
                id = h.Id, packageId = h.PackageId, version = h.Version, state = h.State, code = h.Code, message = h.Message,
                createdAt = h.CreatedAt, createdBy = h.CreatedBy, doneAt = h.DoneAt,
            });
            return Ok(new
            {
                mode = st.IsChild ? "child" : "standalone",
                markAsTest = st.MarkedAsTest,
                master = st.IsChild ? null : new { uats = children, openCodes = codes, handovers = intakes },
                uat = !st.IsChild ? null : new
                {
                    masterUrl = st.MasterUrl, paired = st.PairingId.Length > 0, pairingId = st.PairingId, name = st.Name,
                    masterKeyId = st.MasterKeyId, ownKeyId = st.OwnKeyId, pairedAt = st.PairedAt, lastTest = st.LastTest, keyProblem = st.KeyProblem,
                    // E1b: the two IT policies of a test PC, and the warning when this UAT signs with the production key
                    testPc = new { registryKey = FederationService.PolicyKey, serverUrl = OwnUrl(ctx), trustedSigningKeys = st.TrustedSigningKeys },
                    sameSigningKeyAsProduction = st.SameSigningKey,
                    handovers,
                },
            });
        });

        // E1b: .reg for test PCs of this UAT (to=uat sets the policies, to=production removes them)
        adm.MapGet("/reg", async (string? to, HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            if (await AdminAsync(ctx, users) is null) return NotAdmin();
            var st = await svc.ChildStateAsync();
            if (!st.IsChild) return Err("FED_NOT_UAT", "Only a UAT gives test PCs its policies.", 409);
            var uat = to != "production";
            return Results.File(FederationService.RegFile(uat, OwnUrl(ctx), st.TrustedSigningKeys), "application/octet-stream",
                                uat ? "addon-store-use-uat.reg" : "addon-store-use-production.reg");
        });

        adm.MapPut("/mode", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            var j = Json(await ReadBodyAsync(ctx, 4096) ?? Array.Empty<byte>());
            var mode = S(j, "mode");
            if (mode is not ("child" or "standalone")) return Err("FED_MODE_INVALID", "Send {\"mode\": \"child\"} or {\"mode\": \"standalone\"}.");
            bool? mark = j is { } o && o.TryGetProperty("markAsTest", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? m.ValueKind == JsonValueKind.True : null;
            return Answer(await svc.SetModeAsync(mode == "child", admin.DisplayName, mark));
        });

        adm.MapPost("/codes", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            var j = Json(await ReadBodyAsync(ctx, 4096) ?? Array.Empty<byte>());
            return Answer(await svc.CreateCodeAsync(S(j, "name"), admin.DisplayName, admin.Id));
        });

        adm.MapDelete("/codes/{id}", async (string id, HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.RevokeCodeAsync(id, admin.DisplayName));
        });

        adm.MapPost("/uats/{id}/block", async (string id, HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.BlockChildAsync(id, true, admin.DisplayName));
        });

        adm.MapDelete("/uats/{id}/block", async (string id, HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.BlockChildAsync(id, false, admin.DisplayName));
        });

        adm.MapDelete("/uats/{id}", async (string id, HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.RemoveChildAsync(id, admin.DisplayName));
        });

        adm.MapPost("/pair", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc, SettingsService settings) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            var j = Json(await ReadBodyAsync(ctx, 4096) ?? Array.Empty<byte>());
            var own = (await settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');
            if (own.Length == 0) own = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Answer(await svc.PairWithMasterAsync(S(j, "masterUrl"), S(j, "code"), own, admin.DisplayName));
        });

        adm.MapPost("/test", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.TestConnectionAsync(admin.DisplayName));
        });

        adm.MapDelete("/pairing", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            return Answer(await svc.UnpairAsync(admin.DisplayName));
        });
    }
}

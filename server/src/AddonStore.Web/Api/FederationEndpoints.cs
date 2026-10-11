using System.Collections.Concurrent;
using System.Security.Claims;
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
            return Ok(new
            {
                mode = st.IsChild ? "child" : "standalone",
                master = st.IsChild ? null : new { uats = children, openCodes = codes },
                uat = !st.IsChild ? null : new
                {
                    masterUrl = st.MasterUrl, paired = st.PairingId.Length > 0, pairingId = st.PairingId, name = st.Name,
                    masterKeyId = st.MasterKeyId, ownKeyId = st.OwnKeyId, pairedAt = st.PairedAt, lastTest = st.LastTest, keyProblem = st.KeyProblem,
                },
            });
        });

        adm.MapPut("/mode", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            var j = Json(await ReadBodyAsync(ctx, 4096) ?? Array.Empty<byte>());
            var mode = S(j, "mode");
            if (mode is not ("child" or "standalone")) return Err("FED_MODE_INVALID", "Send {\"mode\": \"child\"} or {\"mode\": \"standalone\"}.");
            return Answer(await svc.SetModeAsync(mode == "child", admin.DisplayName));
        });

        adm.MapPost("/codes", async (HttpContext ctx, UserManager<AppUser> users, FederationService svc) =>
        {
            var admin = await AdminAsync(ctx, users);
            if (admin is null) return NotAdmin();
            var j = Json(await ReadBodyAsync(ctx, 4096) ?? Array.Empty<byte>());
            return Answer(await svc.CreateCodeAsync(S(j, "name"), admin.DisplayName));
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

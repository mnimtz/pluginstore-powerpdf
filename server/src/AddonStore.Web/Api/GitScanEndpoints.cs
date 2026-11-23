using System.Security.Claims;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Services.GitScan;
using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Api;

/// <summary>
/// "Git & Veracode" (S2.8.0): webhook of the git server, the list of add-ons out of service for store clients,
/// and taking an add-on out of service by API (admins; emergency users may take out, never put back).
/// </summary>
public static class GitScanEndpoints
{
    public static void MapGitScan(this IEndpointRouteBuilder app)
    {
        // Git server -> store. Anonymous, but nothing is read before the signature is checked (GitScanService.ReceiveAsync).
        app.MapPost("/api/git/webhook", async (HttpContext ctx, GitScanService svc) =>
        {
            if (!await svc.AcceptsWebhooksAsync()) return Fail("NOT_FOUND", "Not found.", 404);   // nothing read while off
            if (ctx.Request.ContentLength is > GitScanService.MaxWebhookBytes) return Fail("PAYLOAD_TOO_LARGE", "Webhook body larger than 1 MB.", 413);
            using var ms = new MemoryStream();
            var buffer = new byte[16384];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + n > GitScanService.MaxWebhookBytes) return Fail("PAYLOAD_TOO_LARGE", "Webhook body larger than 1 MB.", 413);
                ms.Write(buffer, 0, n);
            }
            var r = await svc.ReceiveAsync(ctx.Request.Headers, ms.ToArray());
            return r.Status switch
            {
                404 => Fail("NOT_FOUND", "Not found.", 404),
                401 => Fail("SIGNATURE_INVALID", "The webhook signature is missing or wrong.", 401),
                400 => Fail("DELIVERY_ID_MISSING", "The header X-GitHub-Delivery is missing.", 400),
                _ => Results.Json(new { ok = true, data = new { result = r.Result } }, statusCode: r.Status),
            };
        }).DisableAntiforgery();

        // Store clients (2.3.0+) read this at start: "disabled" = switch the installed add-on off until it is back.
        // Package ids as SHA-256 like /api/blocked, so private add-ons stay unnamed.
        app.MapGet("/api/suspended", async (HttpContext ctx, AppDbContext db) =>
        {
            var all = await SuspensionService.AllAsync(db);
            var list = all.Select(kv => new { hash = Hash(kv.Key), level = kv.Value.Level, reason = kv.Value.Reason, since = kv.Value.Since })
                .OrderBy(e => e.hash).ToList();
            if (ctx.Request.Query["format"] == "tsv")
            {
                var sb = new System.Text.StringBuilder("# hash\tlevel\treason\n");
                foreach (var e in list)
                    sb.Append(e.hash).Append('\t').Append(e.level).Append('\t')
                      .Append(new string(e.reason.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray())).Append('\n');
                return Results.Text(sb.ToString(), "text/tab-separated-values; charset=utf-8");
            }
            return Results.Json(new { ok = true, data = new { hash = "sha256(lowercase package id)", entries = list } });
        });

        app.MapPost("/api/packages/{id}/suspend", async (string id, HttpContext ctx, UserManager<AppUser> users, SuspensionService svc,
                                                          GitScanConfig config, TimeDisplay time) =>
        {
            var user = await UserAsync(ctx, users);
            if (user is null) return Fail("UNAUTHORIZED", "Sign in with an API token.", 401);
            SuspendBody? body;
            try { body = await JsonSerializer.DeserializeAsync<SuspendBody>(ctx.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { body = null; }
            if (body is null) return Fail("SUSPEND_BODY_INVALID", "Send {\"level\": \"paused\"|\"disabled\", \"reason\": \"...\", \"reviewAt\": \"yyyy-mm-dd\", \"notifyCustomers\": true}.", 400);
            DateTime? reviewAt = null;
            if (!string.IsNullOrWhiteSpace(body.ReviewAt))
            {
                if (!DateTime.TryParseExact(body.ReviewAt.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var ra))
                    return Fail("REVIEW_DATE_INVALID", "reviewAt is a date such as 2026-12-31.", 400);
                reviewAt = time.ToUtc(ra.AddHours(8));   // 08:00 store time, like the page
            }
            var r = await svc.SuspendAsync(id, body.Level ?? "", body.Reason, reviewAt, body.NotifyCustomers ?? await config.NotifyCustomersDefaultAsync(),
                                           user, ctx.User.IsInRole("Admin"));
            if (!r.Ok) return Fail(r.Message.StartsWith("Only ") ? "SUSPEND_FORBIDDEN" : r.Message == "Unknown add-on." ? "PACKAGE_NOT_FOUND" : "SUSPEND_INVALID",
                                   r.Message, r.Message.StartsWith("Only ") ? 403 : r.Message == "Unknown add-on." ? 404 : 400);
            return Results.Json(new { ok = true, data = new { id, level = body.Level, message = r.Message } });
        }).RequireAuthorization("BearerOnly");

        app.MapDelete("/api/packages/{id}/suspend", async (string id, HttpContext ctx, UserManager<AppUser> users, SuspensionService svc) =>
        {
            var user = await UserAsync(ctx, users);
            if (user is null) return Fail("UNAUTHORIZED", "Sign in with an API token.", 401);
            var r = await svc.LiftAsync(id, user, ctx.User.IsInRole("Admin"));
            if (!r.Ok) return Fail(r.Message.StartsWith("Only ") ? "ADMIN_ONLY" : "NOT_SUSPENDED", r.Message, r.Message.StartsWith("Only ") ? 403 : 404);
            return Results.Json(new { ok = true, data = new { id, suspended = false, message = r.Message } });
        }).RequireAuthorization("BearerOnly");

        app.MapPost("/api/packages/{id}/rollback", async (string id, HttpContext ctx, UserManager<AppUser> users, SuspensionService svc) =>
        {
            var user = await UserAsync(ctx, users);
            if (user is null) return Fail("UNAUTHORIZED", "Sign in with an API token.", 401);
            if (!ctx.User.IsInRole("Admin")) return Fail("ADMIN_ONLY", "Only admins go back to an earlier version.", 403);
            var r = await svc.RollbackAsync(id, user, true);
            if (!r.Ok) return Fail("ROLLBACK_NOT_POSSIBLE", r.Message, 409);
            return Results.Json(new { ok = true, data = new { id, message = r.Args is { } a ? string.Format(r.Message, a) : r.Message } });
        }).RequireAuthorization("BearerOnly");
    }

    private record SuspendBody(string? Level, string? Reason, string? ReviewAt, bool? NotifyCustomers);

    public static string Hash(string id) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(id.Trim().ToLowerInvariant()))).ToLowerInvariant();

    private static async Task<AppUser?> UserAsync(HttpContext ctx, UserManager<AppUser> users)
    {
        var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null) return null;
        var user = await users.FindByIdAsync(id);
        return user is { Status: UserStatus.Active } ? user : null;
    }

    private static IResult Fail(string code, string message, int status) =>
        Results.Json(new { ok = false, error = new { code, message } }, statusCode: status);
}

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using AddonStore.Web.Data;
using AddonStore.Web.Services;

namespace AddonStore.Web.Api;

/// <summary>
/// "Senden an" API (/api/sendto/*) for the Power PDF add-on, authenticated
/// with a device token ("Authorization: Bearer stdev_..."), plus the
/// invitation page /sendto/invite/{token}. Answer envelope like the rest of
/// the API: {ok, data} or {ok:false, error:{code, message}}.
/// </summary>
public static class SendToEndpoints
{
    // Development only: the last invitation mails with their links (tests).
    private static readonly ConcurrentQueue<object> DevOutbox = new();

    // S1.17.3: limits per client address on the calls a script could repeat cheaply. Generous enough for a company
    // behind one NAT address that installs on many PCs at once.
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
    private static IResult TooMany() => Err("RATE_LIMITED", "Too many requests from this address; try again later.", 429);

    private static IResult Ok(object? data = null) => Results.Json(new { ok = true, data });
    private static IResult Err(string code, string message, int status = 400) =>
        Results.Json(new { ok = false, error = new { code, message } }, statusCode: status);

    private static async Task<JsonElement?> BodyAsync(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string S(JsonElement? b, string k) =>
        b is { } e && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement? b, string k) =>
        b is { } e && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> Strings(JsonElement? b, string k)
    {
        var r = new List<string>();
        if (b is { } e && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) r.Add(x.GetString() ?? "");
        return r;
    }

    /// <summary>Master switch, device token, blocked user; then the handler. Errors become the envelope.</summary>
    private static async Task<IResult> Device(HttpContext ctx, SendToService svc, Func<SendToDevice, Task<IResult>> handler)
    {
        try
        {
            if (!await svc.EnabledAsync()) return Err("SENDTO_DISABLED", "Send to is switched off.", 503);
            var h = ctx.Request.Headers.Authorization.ToString();
            if (!h.StartsWith("Bearer stdev_", StringComparison.Ordinal)) return Err("UNAUTHORIZED", "Missing or unknown device token.", 401);
            var dev = await svc.DeviceByTokenAsync(h[7..].Trim());
            if (dev is null) return Err("UNAUTHORIZED", "Missing or unknown device token.", 401);
            var u = await svc.UserAsync(dev.UserId);
            if (u is null || u.BlockedAt is not null) return Err("USER_BLOCKED", "This user is blocked.", 403);
            await svc.TouchAsync(dev);
            return await handler(dev);
        }
        catch (SendToError e) { return Err(e.Code, e.Message, e.Status); }
    }

    public static void MapSendTo(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sendto");

        g.MapGet("/config", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async _ => Ok((await svc.ConfigAsync()).Public())));

        // Registration: no device token yet, only the master switch applies.
        g.MapPost("/devices", async (HttpContext ctx, SendToService svc) =>
        {
            if (!await svc.EnabledAsync()) return Err("SENDTO_DISABLED", "Send to is switched off.", 503);
            if (Limited(ctx, "register", 200, TimeSpan.FromHours(1))) return TooMany();
            var b = await BodyAsync(ctx);
            try
            {
                var (dev, token) = await svc.RegisterAsync(S(b, "email"), S(b, "name"), S(b, "nameSource"), S(b, "kemPub"), S(b, "sigPub"), null);
                return Ok(new { deviceId = dev.Id, userId = dev.UserId, token });
            }
            catch (SendToError e) { return Err(e.Code, e.Message, e.Status); }
        });

        g.MapDelete("/devices/{id}", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.RemoveDeviceAsync(dev, id); return Ok(); }));

        g.MapGet("/me", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.MeAsync(dev))));

        g.MapMethods("/me", new[] { "PATCH" }, (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx);
            return Ok(new { name = await svc.SetNameAsync(dev, S(b, "name"), Bool(b, "reset")) });
        }));

        g.MapGet("/contacts", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.ContactsAsync(dev.UserId))));
        g.MapDelete("/contacts/{userId}", (string userId, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.RemoveContactAsync(dev.UserId, userId); return Ok(); }));
        g.MapPost("/contacts/{userId}/block", (string userId, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.BlockAsync(dev.UserId, userId, null); return Ok(); }));
        g.MapPost("/contacts/{userId}/report", (string userId, HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            if (Limited(ctx, "report", 30, TimeSpan.FromHours(1))) return TooMany();
            var b = await BodyAsync(ctx);
            await svc.BlockAsync(dev.UserId, userId, S(b, "reason"));
            return Ok();
        }));
        g.MapGet("/contacts/{userId}/devices", (string userId, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => Ok(await svc.DevicesOfContactAsync(dev.UserId, userId))));

        g.MapPost("/invitations", (HttpContext ctx, SendToService svc, NotificationService notify, IWebHostEnvironment env) => Device(ctx, svc, async dev =>
        {
            if (Limited(ctx, "invite", 300, TimeSpan.FromHours(1))) return TooMany();
            var b = await BodyAsync(ctx);
            var emails = Strings(b, "emails");
            if (emails.Count == 0) return Err("EMAILS_MISSING", "emails[] is required.");
            if (emails.Count > 50) return Err("TOO_MANY", "At most 50 addresses per call.");
            var lang = S(b, "lang");
            var results = new List<object>();
            foreach (var e in emails)
            {
                var (result, mail) = await svc.InviteAsync(dev.UserId, e, lang);
                if (mail is { } m)
                {
                    var sent = await SendInvitationAsync(m.inv, m.token, m.from, notify, env);
                    if (!sent) { results.Add(new { email = m.inv.ToEmail, status = "invited", code = "MAIL_NOT_SENT" }); continue; }
                }
                results.Add(result);
            }
            return Ok(results);
        }));

        g.MapGet("/invitations", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.InvitationsAsync(dev.UserId))));
        g.MapPost("/invitations/{id}/accept", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.AnswerAsync(id, dev.UserId, true); return Ok(); }));
        g.MapPost("/invitations/{id}/decline", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.AnswerAsync(id, dev.UserId, false); return Ok(); }));
        g.MapDelete("/invitations/{id}", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.WithdrawAsync(id, dev.UserId); return Ok(); }));

        g.MapGet("/lists", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.ListsAsync(dev.UserId))));
        g.MapPost("/lists", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx);
            return Ok(await svc.SaveListAsync(dev.UserId, null, S(b, "name"), Strings(b, "members"), Bool(b, "favorite")));
        }));
        g.MapPut("/lists/{id}", (string id, HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx);
            return Ok(await svc.SaveListAsync(dev.UserId, id, S(b, "name"), Strings(b, "members"), Bool(b, "favorite")));
        }));
        g.MapDelete("/lists/{id}", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.DeleteListAsync(dev.UserId, id); return Ok(); }));

        g.MapGet("/quick", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.QuickAsync(dev.UserId))));
        g.MapPost("/quick", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx) ?? default;
            return Ok(await svc.SaveQuickAsync(dev.UserId, null, b));
        }));
        g.MapPut("/quick/{id}", (string id, HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx) ?? default;
            return Ok(await svc.SaveQuickAsync(dev.UserId, id, b));
        }));
        g.MapDelete("/quick/{id}", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.DeleteQuickAsync(dev.UserId, id); return Ok(); }));

        g.MapPost("/transfers", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var b = await BodyAsync(ctx);
            if (b is not { } e) return Err("TRANSFER_INVALID", "JSON body required.");
            long size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : -1;
            int chunks = e.TryGetProperty("chunkCount", out var c) && c.TryGetInt32(out var cv) ? cv : -1;
            var envs = new List<SendToService.EnvelopeIn>();
            if (e.TryGetProperty("envelopes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var x in arr.EnumerateArray())
                    envs.Add(new SendToService.EnvelopeIn(S(x, "deviceId"), S(x, "envelope"), S(x, "signature")));
            var (id, after) = await svc.CreateTransferAsync(dev, size, chunks, envs);
            return Ok(new { transferId = id, deliverAfter = after });
        }));

        g.MapPut("/transfers/{id}/chunks/{n:int}", (string id, int n, HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            await svc.PutChunkAsync(dev, id, n, ms.ToArray());
            return Ok();
        }));
        g.MapPost("/transfers/{id}/cancel", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.CancelAsync(dev, id); return Ok(); }));
        g.MapGet("/inbox", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.InboxAsync(dev))));
        g.MapGet("/sent", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.SentAsync(dev.UserId))));
        g.MapGet("/transfers/{id}/chunks/{n:int}", (string id, int n, HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev =>
        {
            var bytes = await svc.GetChunkAsync(dev, id, n);
            return bytes is null ? Err("CHUNK_NOT_FOUND", "Unknown transfer or chunk.", 404) : Results.Bytes(bytes, "application/octet-stream");
        }));
        g.MapPost("/transfers/{id}/accept", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.ResolveAsync(dev, id, "accepted"); return Ok(); }));
        g.MapPost("/transfers/{id}/decline", (string id, HttpContext ctx, SendToService svc) =>
            Device(ctx, svc, async dev => { await svc.ResolveAsync(dev, id, "declined"); return Ok(); }));

        // Polling stand-in for Server-Sent Events: "anything new?" counters.
        g.MapGet("/events", (HttpContext ctx, SendToService svc) => Device(ctx, svc, async dev => Ok(await svc.EventsAsync(dev))));

        // ---- invitation page. GET only shows; accepting needs the button (POST):
        // mail scanners open links automatically and must not accept anything.
        app.MapGet("/sendto/invite/{token}", async (string token, SendToService svc, IDataProtectionProvider dp) =>
            Results.Content(await InvitePageAsync(token, svc, dp, null, done: null), "text/html; charset=utf-8"));

        app.MapPost("/sendto/invite/{token}", async (string token, HttpContext ctx, SendToService svc, IDataProtectionProvider dp) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var inv = await svc.InvitationByTokenAsync(token);
            if (inv is null || !CsrfValid(dp, token, form["csrf"].ToString()))
                return Results.Content(await InvitePageAsync(token, svc, dp, inv, done: null), "text/html; charset=utf-8");
            var accept = form["action"] == "accept";
            var okAnswer = await svc.EnabledAsync() && await svc.AnswerByTokenAsync(inv, accept);
            return Results.Content(await InvitePageAsync(token, svc, dp, inv, done: okAnswer ? (accept ? "accepted" : "declined") : "not_open"),
                                   "text/html; charset=utf-8");
        });

        // Development only: invitation mails with their links, for the e2e test.
        if (app is WebApplication wa && wa.Environment.IsDevelopment())
        {
            app.MapGet("/sendto/dev/mails", () => Ok(DevOutbox.ToArray()));
            // test switches: {"Enabled": "false", "UndoSeconds": "0", ...} -> SendTo.<key>
            app.MapPut("/sendto/dev/config", async (HttpContext ctx, SettingsService settings, SendToService svc) =>
            {
                var b = await BodyAsync(ctx);
                if (b is { } e)
                    foreach (var p in e.EnumerateObject())
                        await settings.SetAsync("SendTo." + p.Name, p.Value.ToString());
                return Ok((await svc.ConfigAsync()).Public());
            });
        }
    }

    private static async Task<bool> SendInvitationAsync(SendToInvitation inv, string token, SendToUser from, NotificationService notify,
                                                        IWebHostEnvironment env)
    {
        var baseUrl = await notify.SecureBaseUrlAsync();
        if (baseUrl is null) return false;   // never a secret link to an untrusted host
        var link = $"{baseUrl}/sendto/invite/{token}";
        var t = SendToTexts.For(inv.Lang);
        var culture = System.Globalization.CultureInfo.GetCultureInfo(inv.Lang == "nb" ? "nb-NO" : inv.Lang);
        var date = inv.ExpiresAt.ToString("d", culture);
        string H(string s) => WebUtility.HtmlEncode(s);
        var subject = SendToTexts.Fill(t.Subject, from.Name, from.Email, date);
        var dir = SendToTexts.IsRtl(inv.Lang) ? " dir=\"rtl\"" : "";
        var html = $"""
            <div{dir}>
            <p>{H(SendToTexts.Fill(t.Intro, from.Name, from.Email, date))}</p>
            <p><a href="{H(link)}" style="display:inline-block;background:#00A0FB;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;font-weight:bold">{H(t.Button)}</a></p>
            <p style="color:#6b7a8c;font-size:13px">{H(SendToTexts.Fill(t.Footer, from.Name, from.Email, date))}</p>
            </div>
            """;
        if (env.IsDevelopment())
        {
            DevOutbox.Enqueue(new { to = inv.ToEmail, subject, link, invitationId = inv.Id, at = DateTime.UtcNow });
            while (DevOutbox.Count > 200) DevOutbox.TryDequeue(out _);
        }
        var r = await notify.SendDirectAsync(inv.ToEmail, subject, html, "SendToInvite");
        return r.Sent || env.IsDevelopment();
    }

    // Form value of the invitation page: the token protected with the store's
    // data protection keys (data/keys), valid for 12 hours.
    private static ITimeLimitedDataProtector Csrf(IDataProtectionProvider dp) =>
        dp.CreateProtector("AddonStore.SendToInvite").ToTimeLimitedDataProtector();

    private static bool CsrfValid(IDataProtectionProvider dp, string token, string value)
    {
        try { return Csrf(dp).Unprotect(value) == token; }
        catch (CryptographicException) { return false; }
    }

    private static async Task<string> InvitePageAsync(string token, SendToService svc, IDataProtectionProvider dp, SendToInvitation? inv, string? done)
    {
        inv ??= await svc.InvitationByTokenAsync(token);
        var t = SendToTexts.For(inv?.Lang);
        string H(string s) => WebUtility.HtmlEncode(s);
        string body;
        var from = inv is null ? null : await svc.UserAsync(inv.FromUserId);
        var fromName = from?.Name ?? "";
        var fromEmail = from?.Email ?? "";
        if (!await svc.EnabledAsync()) body = $"<h1>{H(t.Title)}</h1><p>{H(t.Unavailable)}</p>";
        else if (inv is null || from is null) body = $"<h1>{H(t.NotFoundTitle)}</h1><p>{H(t.NotFoundText)}</p>";
        else if (done == "accepted") body = $"<h1>{H(t.AcceptedTitle)}</h1><p>{H(SendToTexts.Fill(t.AcceptedText, fromName, fromEmail, ""))}</p>";
        else if (done == "declined") body = $"<h1>{H(t.DeclinedTitle)}</h1><p>{H(t.DeclinedText)}</p>";
        else if (inv.Status != "open" || inv.ExpiresAt < DateTime.UtcNow || done == "not_open") body = $"<h1>{H(t.Title)}</h1><p>{H(t.NotOpenText)}</p>";
        else
        {
            var csrf = Csrf(dp).Protect(token, TimeSpan.FromHours(12));
            body = $"""
                <h1>{H(t.Title)}</h1>
                <p>{H(SendToTexts.Fill(t.Ask, fromName, fromEmail, ""))}</p>
                <form method="post">
                  <input type="hidden" name="csrf" value="{H(csrf)}">
                  <button name="action" value="accept" class="p">{H(t.Accept)}</button>
                  <button name="action" value="decline">{H(t.Decline)}</button>
                </form>
                """;
        }
        var lang = inv?.Lang ?? "en";
        var dir = SendToTexts.IsRtl(lang) ? "rtl" : "ltr";
        return $$"""
            <!doctype html><html lang="{{H(lang)}}" dir="{{dir}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="robots" content="noindex,nofollow"><title>{{H(t.Title)}}</title>
            <style>
            :root{--navy:#002854;--blue:#00A0FB;--ink:#1f2a37;--line:#e1e7ee}
            body{margin:0;font-family:"Red Hat Display","Segoe UI",Arial,sans-serif;color:var(--ink);background:#f2f6fa}
            .bar{background:var(--navy);color:#fff;padding:14px 20px;font-weight:700}.line{height:4px;background:linear-gradient(90deg,#00EB86,#00A0FB)}
            main{max-width:540px;margin:40px auto;padding:28px;background:#fff;border:1px solid var(--line);border-radius:10px}
            h1{color:var(--navy);font-size:22px;margin:0 0 12px}
            button{font:inherit;padding:9px 18px;border-radius:6px;border:1px solid var(--line);background:#fff;cursor:pointer;margin-inline-end:8px}
            button.p{background:var(--blue);border-color:var(--blue);color:#fff;font-weight:700}
            </style></head><body><div class="bar">Tungsten Power PDF</div><div class="line"></div><main>{{body}}</main></body></html>
            """;
    }

}

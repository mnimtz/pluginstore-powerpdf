using AddonStore.Web.Auth;
using Microsoft.AspNetCore.Authentication;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.6.0: in maintenance mode every request that changes data (POST, PUT, PATCH, DELETE) is paused for everybody
/// except admins (sign-in cookie or admin API token). Reading stays open: catalog, add-on pages, downloads and
/// installations from Power PDF. Also open: signing in and out (admins must get in), the dry-run package check, the
/// share click counter, and two housekeeping calls of the store client (the daily state and the release of seats on
/// uninstall), so no license seat stays taken. The API answers 503 SERVER_MAINTENANCE with Retry-After; web forms
/// lead to /Maintenance, which explains it.
/// </summary>
public static class MaintenanceGate
{
    // S2.7.1 (review): exact paths, never prefixes: a package id such as "validator.tools" would otherwise open
    // every write under /api/packages/validator.tools/...
    private static readonly HashSet<string> OpenPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Login", "/Account/Logout", "/signin-oidc",
        "/api/packages/validate", "/api/client/inventory", "/api/deliveries/release",
        // S2.8.0: git webhooks are not sent again by the git server; checked by their signature, they only write the event log
        "/api/git/webhook",
    };
    // S2.8.0: the emergency brake works in maintenance too (the handler itself checks admin or emergency user)
    private static readonly System.Text.RegularExpressions.Regex SuspendPath = new(@"^/api/packages/[A-Za-z0-9._-]{1,200}/suspend\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Taking an add-on out of service (POST only), by API or on the Git &amp; Veracode page.</summary>
    public static bool IsEmergencyBrake(HttpRequest r)
    {
        if (!HttpMethods.IsPost(r.Method)) return false;
        var p = (r.Path.Value ?? "").TrimEnd('/');
        if (string.Equals(p, "/Admin/GitScan", StringComparison.OrdinalIgnoreCase) && string.Equals(r.Query["handler"], "Suspend", StringComparison.OrdinalIgnoreCase)) return true;
        try { return SuspendPath.IsMatch(p); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
    }
    private static readonly System.Text.RegularExpressions.Regex ClickPath = new(@"^/a/[A-Za-z0-9._-]{1,120}/click\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>A request that may change data.</summary>
    public static bool IsWrite(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>Paths that stay open in maintenance (see the class summary).</summary>
    public static bool IsOpen(PathString path)
    {
        var p = (path.Value ?? "").TrimEnd('/');
        if (OpenPaths.Contains(p)) return true;
        // the share click counter of an add-on page: /a/{slug}/click
        try { return ClickPath.IsMatch(p); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Seconds for Retry-After: until the expected end (at most a day), else a quarter of an hour.</summary>
    public static int RetryAfterSeconds(DateTime? until, DateTime nowUtc) =>
        until is { } u && u > nowUtc ? (int)Math.Clamp((u - nowUtc).TotalSeconds, 60, 86400) : 900;

    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        if (!IsWrite(ctx.Request.Method) || IsOpen(ctx.Request.Path) || IsEmergencyBrake(ctx.Request)) { await next(); return; }
        var settings = ctx.RequestServices.GetRequiredService<SettingsService>();
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        if (await ServerMode.GetAsync(settings, config) != ServerMode.Maintenance) { await next(); return; }
        if (await IsAdminAsync(ctx)) { await next(); return; }

        var until = await ServerMode.UntilAsync(settings);
        ctx.Response.Headers.RetryAfter = RetryAfterSeconds(until, DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = ctx.Request.Path;
        // the SendTo invitation page is a browser form: it gets the explanation page like the other web forms
        if (path.StartsWithSegments("/api") || (path.StartsWithSegments("/sendto") && !path.StartsWithSegments("/sendto/invite")))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new
            {
                ok = false,
                error = new
                {
                    code = "SERVER_MAINTENANCE",
                    message = "The store is in maintenance: uploads, registrations and other changes are paused. The catalog, downloads and installations keep working.",
                    hint = until is { } u ? $"Try again after {u:yyyy-MM-ddTHH:mm}Z." : "Try again later; GET /api/ping reports serverMode.",
                    until,
                },
            });
            return;
        }
        // a web form: the explanation page (303: the browser follows with GET)
        ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
        ctx.Response.Headers.Location = "/Maintenance";
    }

    /// <summary>An admin by sign-in cookie, or by an admin API token in the Authorization header.</summary>
    private static async Task<bool> IsAdminAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole("Admin")) return true;
        if (!ctx.Request.Headers.ContainsKey("Authorization")) return false;
        var r = await ctx.AuthenticateAsync(ApiTokenAuthHandler.Scheme);
        return r.Succeeded && r.Principal?.IsInRole("Admin") == true;
    }
}

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
    private static readonly string[] OpenPrefixes =
    {
        "/Account/Login", "/Account/Logout", "/signin-oidc",
        "/api/packages/validate", "/api/client/inventory", "/api/deliveries/release",
    };

    /// <summary>A request that may change data.</summary>
    public static bool IsWrite(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>Paths that stay open in maintenance (see the class summary).</summary>
    public static bool IsOpen(PathString path)
    {
        var p = path.Value ?? "";
        if (OpenPrefixes.Any(o => p.StartsWith(o, StringComparison.OrdinalIgnoreCase))) return true;
        // the share click counter of an add-on page: /a/{slug}/click
        return p.StartsWith("/a/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/click", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seconds for Retry-After: until the expected end (at most a day), else a quarter of an hour.</summary>
    public static int RetryAfterSeconds(DateTime? until, DateTime nowUtc) =>
        until is { } u && u > nowUtc ? (int)Math.Clamp((u - nowUtc).TotalSeconds, 60, 86400) : 900;

    public static async Task Middleware(HttpContext ctx, Func<Task> next)
    {
        if (!IsWrite(ctx.Request.Method) || IsOpen(ctx.Request.Path)) { await next(); return; }
        var settings = ctx.RequestServices.GetRequiredService<SettingsService>();
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        if (await ServerMode.GetAsync(settings, config) != ServerMode.Maintenance) { await next(); return; }
        if (await IsAdminAsync(ctx)) { await next(); return; }

        var until = await ServerMode.UntilAsync(settings);
        ctx.Response.Headers.RetryAfter = RetryAfterSeconds(until, DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/sendto"))
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

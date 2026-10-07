namespace AddonStore.Web.Services;

/// <summary>
/// Navigation of the signed-in area (S1.5.0 preview): the classic top bar, or the new
/// sidebar with grouped sections and a start page with tiles. Each user switches in the
/// avatar menu or the footer (cookie); the classic bar stays the default, so the new one
/// can be tried without risk and switched off with one click. Public pages (catalog,
/// add-on pages, disclaimer) always keep the classic layout.
/// </summary>
public static class Shell
{
    public const string Cookie = "pp_shell";

    public static bool Sidebar(HttpContext ctx) => ctx.Request.Cookies[Cookie] == "sidebar";

    /// <summary>Same-site relative target only (as /set-lang): "//host", "/\host" or control characters fall back to "/".</summary>
    public static string SafeTarget(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") &&
        !returnUrl.Any(ch => char.IsControl(ch) || ch == '\\' || char.IsWhiteSpace(ch)) &&
        !returnUrl.Replace("\t", "").Contains("//")
            ? returnUrl : "/";
}

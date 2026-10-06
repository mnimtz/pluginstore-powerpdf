using Microsoft.AspNetCore.DataProtection;
using System.Globalization;
using AddonStore.Web.Api;
using AddonStore.Web.Auth;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- data ---------------------------------------------------------------
var dataDir = builder.Configuration["Storage:Data"];
if (string.IsNullOrWhiteSpace(dataDir))
    dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var connection = builder.Configuration.GetConnectionString("Default")
                 ?? $"Data Source={Path.Combine(dataDir, "pluginstore.db")}";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
// Keys for auth cookies and antiforgery tokens live in the persistent data
// folder; in the container's default location they were lost on every restart,
// which signed everybody out after each deployment.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("AddonStore");

// --- identity + token auth ----------------------------------------------
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
        o.User.RequireUniqueEmail = true;
    })
    .AddErrorDescriber<AddonStore.Web.Services.LocalizedIdentityErrors>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
// password reset links (S1.0.6): valid 24 hours, single use via the security stamp
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = AddonStore.Web.Services.PasswordResetService.Lifetime);

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Login";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    // Production runs behind HTTPS only; the local test server uses plain http.
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
// Sessions are re-checked against the security stamp every minute: disabling
// an account or changing its role takes effect almost at once.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services.AddAuthentication()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthHandler>(
        ApiTokenAuthHandler.Scheme, null);

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("ApiOrCookie", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiTokenAuthHandler.Scheme)
        .RequireAuthenticatedUser());
    o.AddPolicy("AdminOnly", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiTokenAuthHandler.Scheme)
        .RequireRole("Admin"));
    o.AddPolicy("BearerOnly", p => p
        .AddAuthenticationSchemes(ApiTokenAuthHandler.Scheme)
        .RequireAuthenticatedUser());
    o.AddPolicy("ReviewerOrAdmin", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiTokenAuthHandler.Scheme)
        .RequireRole("Admin", "Reviewer"));
    // Web pages authenticate with the cookie ONLY, so an anonymous visitor is
    // redirected to the login page instead of receiving the API's JSON 401.
    o.AddPolicy("PageAdmin", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
        .RequireRole("Admin"));
    o.AddPolicy("PageReviewer", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
        .RequireRole("Admin", "Reviewer"));
    o.AddPolicy("PageUser", p => p
        .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
        .RequireAuthenticatedUser());
});

// --- localization: the 21 Power PDF UI languages (S1.2.0) -------------------
string[] cultures = AddonStore.Web.Services.Lang.Ui;
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("en");
    // Gregorian calendar in every culture (audit S1.3.1): Arabic may default to another calendar,
    // and dates in keys, reports and mails must stay comparable
    o.SupportedCultures = cultures.Select(c =>
    {
        var ci = new CultureInfo(c);
        if (ci.Calendar is not GregorianCalendar) ci.DateTimeFormat.Calendar = new GregorianCalendar();
        return ci;
    }).ToList();
    o.SupportedUICultures = o.SupportedCultures;
    // ?rlang= (report language, admin reports and their PDF view) wins over everything else.
    o.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider
    {
        QueryStringKey = "rlang",
        UIQueryStringKey = "rlang"
    });
    // cookie (manual switch) wins over Accept-Language (automatic detection).
    // Norwegian browsers often send "no" or "nn"; both map to our Bokmål texts.
    o.RequestCultureProviders.Insert(3, new CustomRequestCultureProvider(ctx =>
    {
        var first = ctx.Request.Headers.AcceptLanguage.ToString().Split(',')[0].Split(';')[0].Trim().ToLowerInvariant();
        return Task.FromResult(first is "no" or "nn" or "no-no" or "nn-no"
            ? new ProviderCultureResult("nb") : (ProviderCultureResult?)null);
    }));
});

builder.Services.AddRazorPages(o =>
    {
        // Reviewers may use the review queue; everything else under /Admin is
        // admin-only (users, audit, settings).
        o.Conventions.AuthorizePage("/Admin/Review", "PageReviewer");
        o.Conventions.AuthorizePage("/Admin/Users", "PageAdmin");
        o.Conventions.AuthorizePage("/Admin/Audit", "PageAdmin");
        o.Conventions.AuthorizePage("/Admin/Settings", "PageAdmin");
        o.Conventions.AuthorizePage("/Admin/Backup", "PageAdmin");
        o.Conventions.AuthorizePage("/Admin/Categories", "PageAdmin");
        o.Conventions.AuthorizePage("/Admin/Rules", "PageUser");        // read: everyone signed in; edit: admins (S1.0.9)
        o.Conventions.AuthorizePage("/Admin/Docs", "PageUser");         // every document in one place (S1.0.12)
        o.Conventions.AddPageRoute("/Admin/Docs", "Docs");               // short address for developers (S1.0.13)
        o.Conventions.AuthorizeFolder("/Issues", "PageUser");           // problem report queue: own add-ons, admins all (S1.1.0)
        o.Conventions.AuthorizePage("/Insights", "PageUser");           // developer dashboard (S1.1.0)
        o.Conventions.AuthorizePage("/Dossier", "PageUser");            // audit dossier: owner, admins, reviewers (S1.3.0)
        o.Conventions.AuthorizePage("/Admin/Reports", "PageAdmin");
        o.Conventions.AuthorizePage("/Dashboard", "PageUser");
        o.Conventions.AuthorizePage("/CatalogEntry", "PageUser");
        o.Conventions.AuthorizePage("/Plugin", "PageUser");
        o.Conventions.AuthorizePage("/Customers", "PageUser");
        o.Conventions.AuthorizePage("/Customer", "PageUser");
        o.Conventions.AuthorizePage("/Profile", "PageUser");
        o.Conventions.AuthorizePage("/Developer", "PageUser");
    })
    .AddViewLocalization();

// --- app services ---------------------------------------------------------
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<ShareService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<IssueService>();
builder.Services.AddScoped<InsightsService>();
builder.Services.AddScoped<DossierService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<AiAssist>();
builder.Services.AddScoped<CustomerService>();
// A failing background service (AI worker, geo refresh, maintenance) must never stop the web app.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddHostedService<AiWorker>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GeoService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeoService>());
builder.Services.AddHostedService<UsageMaintenance>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<PasswordResetService>();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("resend", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddSingleton<PackageSigning>();
builder.Services.AddSingleton<SetupGate>();
builder.Services.AddScoped<TimeDisplay>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<CloudBackupService>();
builder.Services.AddHostedService<CloudBackupScheduler>();
builder.Services.AddScoped<PackageMetaService>();
builder.Services.AddScoped<VersionActionService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<SourceService>();
builder.Services.AddScoped<IAppEmailSender, ResendEmailSender>();

var versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION");
var appVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "0.0.0-dev";
builder.Services.AddSingleton(new AppVersion(appVersion));
AddonStore.Web.Validation.RuleCatalog.ServerVersion = appVersion;   // recorded in the rules snapshots (S1.3.0)

builder.Services.Configure<KestrelServerOptions>(o =>
{
    o.Limits.MaxRequestBodySize = 220 * 1024 * 1024;
    o.AddServerHeader = false;          // do not announce the server software
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = 220 * 1024 * 1024);

var app = builder.Build();

// --- schema + roles --------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    // Each start-up step with its duration in the container log (S0.17.1).
    var startLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AddonStore.Startup");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    startLog.LogInformation("Startup: schema upgrade");
    await AddonStore.Web.Data.SchemaUpgrade.RunAsync(scope.ServiceProvider);
    await app.Services.GetRequiredService<SetupGate>().InitAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    await AddonStore.Web.Validation.RuleCatalog.LoadAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    startLog.LogInformation("Startup: database ready after {Ms} ms", clock.ElapsedMilliseconds);
}

// Security response headers. TLS terminates at App Service, which does not
// add HSTS itself; browsers only honour the header on HTTPS responses.
var sendHsts = !app.Environment.IsDevelopment();
app.Use(async (ctx, next) =>
{
    if (sendHsts) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    // Scripts only from this site or inline with this response's nonce (no
    // inline event handlers): injected markup cannot run script.
    var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    ctx.Items["CspNonce"] = nonce;
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        $"script-src 'self' 'nonce-{nonce}'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    await next();
});

// HEAD on the API and the documentation entry points (S1.0.4): link checkers and
// the fetch tools of AI assistants often ask HEAD first and give up on 405.
// Answered like GET, without a body.
app.Use(async (ctx, next) =>
{
    // Documentation only: a HEAD on a download or the catalog must not count as usage.
    var path = ctx.Request.Path.Value ?? "";
    if (HttpMethods.IsHead(ctx.Request.Method) &&
        (path is "/llms.txt" or "/robots.txt" or "/agent-guide" or "/api" or "/api/" or "/api/ping" or "/api/agent-guide"
             or "/api/openapi.json" or "/api/agents-md" or "/api/skill" or "/api/tools/make-ppak.ps1"
             or "/api/agent-guide/checklist" or "/api/agent-guide/manual-upload" or "/api/rules" or "/agent-guide/checklist" or "/agent-guide/manual-upload" || path.StartsWith("/api/schema/", StringComparison.Ordinal)))
    {
        ctx.Request.Method = HttpMethods.Get;
        var body = ctx.Response.Body;
        ctx.Response.Body = Stream.Null;
        try { await next(); }
        finally { ctx.Response.Body = body; }
        return;
    }
    await next();
});

app.UseRequestLocalization();
app.UseStaticFiles();
app.UseRouting();
// Small request bodies everywhere in the API except the package/source uploads:
// anonymous endpoints (ratings, feedback, share clicks) never need megabytes.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var upload = (HttpMethods.IsPost(ctx.Request.Method) && (p.Equals("/api/packages") || p.Equals("/api/packages/validate")))
                 || (HttpMethods.IsPut(ctx.Request.Method) && p.StartsWithSegments("/api/packages") && p.Value!.EndsWith("/source"));
    // web uploads: the package form, the source upload of a version, backup restore
    upload |= HttpMethods.IsPost(ctx.Request.Method) &&
              (p.Equals("/Dashboard") || p.StartsWithSegments("/Admin/Backup") ||
               (p.Equals("/Plugin") && string.Equals(ctx.Request.Query["handler"], "Source", StringComparison.OrdinalIgnoreCase)));
    if (!upload)
    {
        var f = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        // problem reports carry up to 10 MB of attachments as base64 (S1.1.0); the daily limits per
        // installation and network still apply
        var report = HttpMethods.IsPost(ctx.Request.Method) && p.StartsWithSegments("/api/packages") && p.Value!.EndsWith("/feedback");
        if (f is { IsReadOnly: false }) f.MaxRequestBodySize = report ? 15 * 1024 * 1024 : 2 * 1024 * 1024;
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// CSRF guard for the API: a state-changing call that authenticates with the
// sign-in cookie (instead of a bearer token) must carry X-Requested-With.
// Browsers cannot add that header cross-site without CORS, which is off.
app.Use(async (ctx, next) =>
{
    var m = ctx.Request.Method;
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(m) && !HttpMethods.IsHead(m) && !HttpMethods.IsOptions(m) &&
        !ctx.Request.Headers.ContainsKey("Authorization") && ctx.Request.Cookies.Keys.Any(k => k.StartsWith(".AspNetCore.Identity")) &&
        !ctx.Request.Headers.ContainsKey("X-Requested-With"))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = new { code = "CSRF_CHECK",
            message = "Cookie-authenticated API writes need the header X-Requested-With.",
            hint = "Use a bearer token (Authorization: Bearer ppak_...) for API calls." } });
        return;
    }
    await next();
});

// First-run: until the first (admin) account exists, the UI leads to /Setup.
var setupDone = false;
app.Use(async (ctx, next) =>
{
    if (!setupDone && !ctx.Request.Path.StartsWithSegments("/Setup")
                   && !ctx.Request.Path.StartsWithSegments("/api")
                   && !ctx.Request.Path.StartsWithSegments("/css")
                   && !ctx.Request.Path.StartsWithSegments("/img"))
    {
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync())
        {
            ctx.Response.Redirect("/Setup");
            return;
        }
        setupDone = true;
    }
    await next();
});

// Manual language switch; the culture cookie outranks Accept-Language.
app.MapGet("/set-lang", (string culture, string? returnUrl, HttpContext ctx) =>
{
    // Only supported cultures (an arbitrary string would throw), and only
    // same-site relative targets ("//host" and "/\host" are open redirects).
    if (!cultures.Contains(culture)) culture = "en";
    // Browsers drop tabs and line breaks ("/\t/evil.example" becomes "//evil.example"),
    // so any control character, backslash or "//" rejects the target.
    var target = returnUrl is not null && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") &&
                 !returnUrl.Any(ch => char.IsControl(ch) || ch == '\\' || char.IsWhiteSpace(ch)) &&
                 !returnUrl.Replace("\t", "").Contains("//")
        ? returnUrl : "/";
    ctx.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
    return Results.Redirect(target);
});

// Avatars live under data/avatars and are uploaded on the profile page.
app.MapGet("/avatar/{id}", (string id, IConfiguration config, IWebHostEnvironment env) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9-]{1,64}$"))
        return Results.NotFound();
    var dataRoot = config["Storage:Data"];
    if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(env.ContentRootPath, "data");
    foreach (var ext in new[] { "png", "jpg" })
    {
        var p = Path.Combine(dataRoot, "avatars", $"{id}.{ext}");
        if (File.Exists(p))
            return Results.File(p, ext == "png" ? "image/png" : "image/jpeg");
    }
    return Results.NotFound();
});

app.MapRazorPages();
app.MapApi();

app.Run();

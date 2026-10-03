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

// --- identity + token auth ----------------------------------------------
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 8;
        o.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Login";
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

// --- localization: all European Power PDF languages ----------------------
string[] cultures = { "en", "de", "fr", "it", "es", "nl", "pt", "da", "fi", "nb", "sv", "pl", "cs", "hu", "ru", "tr" };
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("en");
    o.SupportedCultures = cultures.Select(c => new CultureInfo(c)).ToList();
    o.SupportedUICultures = o.SupportedCultures;
    // cookie (manual switch) wins over Accept-Language (automatic detection)
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
        o.Conventions.AuthorizePage("/Dashboard", "PageUser");
        o.Conventions.AuthorizePage("/Profile", "PageUser");
        o.Conventions.AuthorizePage("/Developer", "PageUser");
    })
    .AddViewLocalization();

// --- app services ---------------------------------------------------------
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<NotificationService>();

builder.Services.AddHttpClient();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<TimeDisplay>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<IAppEmailSender, ResendEmailSender>();

var versionFile = Path.Combine(AppContext.BaseDirectory, "VERSION");
builder.Services.AddSingleton(new AppVersion(
    File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "0.0.0-dev"));

builder.Services.Configure<KestrelServerOptions>(o =>
    o.Limits.MaxRequestBodySize = 220 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = 220 * 1024 * 1024);

var app = builder.Build();

// --- schema + roles --------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Poor-man migrations: EnsureCreated never alters an existing database, so
    // additions arrive as idempotent statements here.
    foreach (var sql in new[]
    {
        "ALTER TABLE AspNetUsers ADD COLUMN AvatarFile TEXT NULL",
        "ALTER TABLE AspNetUsers ADD COLUMN ShowContactPublicly INTEGER NOT NULL DEFAULT 1",
        "CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS Invites (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
            "Email TEXT NOT NULL, TokenHash TEXT NOT NULL, Role TEXT NOT NULL, InvitedBy TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL, AcceptedAt TEXT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Invites_TokenHash ON Invites (TokenHash)"
    })
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* column/table already there */ }
    }

    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Admin", "Reviewer", "User" })
        if (!await roles.RoleExistsAsync(role))
            await roles.CreateAsync(new IdentityRole(role));
}

// Security response headers (TLS/HSTS terminate at App Service).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    await next();
});

app.UseRequestLocalization();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

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
    var target = returnUrl is not null && returnUrl.StartsWith('/') &&
                 !(returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
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

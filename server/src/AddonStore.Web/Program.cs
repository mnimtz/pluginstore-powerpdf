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
        o.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
        o.Conventions.AuthorizePage("/Dashboard");
        o.Conventions.AuthorizePage("/Profile");
    })
    .AddViewLocalization();

// --- app services ---------------------------------------------------------
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<NotificationService>();

if (!string.IsNullOrEmpty(builder.Configuration["Email:ResendApiKey"]))
{
    builder.Services.AddHttpClient<ResendEmailSender>();
    builder.Services.AddScoped<IAppEmailSender>(sp => sp.GetRequiredService<ResendEmailSender>());
}
else
{
    builder.Services.AddScoped<IAppEmailSender, NullEmailSender>();
}

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
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Admin", "User" })
        if (!await roles.RoleExistsAsync(role))
            await roles.CreateAsync(new IdentityRole(role));
}

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
    ctx.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
    return Results.Redirect(string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "/" : returnUrl);
});

app.MapRazorPages();
app.MapApi();

app.Run();

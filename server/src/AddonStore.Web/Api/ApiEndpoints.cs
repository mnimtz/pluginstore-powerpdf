using System.Security.Claims;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Api;

public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/", (AppVersion ver, HttpContext ctx) => Results.Json(new
        {
            ok = true,
            data = new
            {
                name = "PluginStore-PowerPDF",
                version = ver.Value,
                docs = Base(ctx) + "/api/agent-guide",
                endpoints = new[]
                {
                    "GET  /api/agent-guide            full instructions for developers and AI agents (markdown)",
                    "GET  /api/schema/manifest        JSON schema of manifest.json",
                    "GET  /api/me                     verify your token, see your packages (auth)",
                    "GET  /api/catalog?channel=beta   released packages; beta channel includes pre-release versions",
                    "GET  /api/packages/{id}          status and history of one package",
                    "POST /api/packages/validate      dry-run: full validation, nothing stored (auth)",
                    "POST /api/packages               submit a package (auth)",
                    "DELETE /api/packages/{id}/{version}  withdraw your own beta version (auth)",
                    "GET  /api/packages/{id}/{version}/download",
                    "GET  /api/devkit                 SDK documentation and developer kit files"
                }
            }
        }));

        api.MapGet("/ping", (AppVersion ver) => Results.Json(new { ok = true, data = new { name = "pluginstore-powerpdf", version = ver.Value } }));

        api.MapGet("/agent-guide", (HttpContext ctx, AppVersion ver) =>
            Results.Text(AgentGuide.Markdown(Base(ctx), ver.Value), "text/markdown; charset=utf-8"));

        api.MapGet("/schema/manifest", () => Results.Text(AgentGuide.ManifestSchema, "application/json"));

        api.MapGet("/me", async (HttpContext ctx, AppDbContext db, UserManager<AppUser> users) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var roles = await users.GetRolesAsync(user);
            var packages = await db.Packages.Where(p => p.OwnerId == user.Id)
                .Select(p => new
                {
                    id = p.Id,
                    versions = p.Versions.OrderByDescending(v => v.SubmittedAt)
                        .Select(v => new { v.Version, status = v.Status.ToString().ToLowerInvariant(), v.SubmittedAt, v.Downloads })
                }).ToListAsync();
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    user = user.DisplayName,
                    email = user.Email,
                    roles,
                    token = ctx.User.FindFirstValue("token_name"),
                    packages
                }
            });
        }).RequireAuthorization("ApiOrCookie");

        api.MapGet("/catalog", async (AppDbContext db, HttpContext ctx, string? channel, string? format, string? lang) =>
        {
            var beta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);

            // TSV variant for native clients (the Power PDF ribbon add-on): one
            // line per package, text fields with tabs/newlines flattened.
            if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
            {
                var culture = (lang ?? "en").Trim();
                if (culture.Length > 2) culture = MapHostLang(culture);
                var items = await Services.CatalogUi.GetAsync(db, culture, beta);
                static string Flat(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                var sb = new System.Text.StringBuilder();
                foreach (var i in items)
                {
                    sb.Append(i.Id).Append('\t').Append(i.Version).Append('\t').Append(i.Channel).Append('\t')
                      .Append(Flat(i.Name)).Append('\t').Append(Flat(i.Description)).Append('\t')
                      .Append(Flat(i.Changelog)).Append('\t').Append(i.MinHost).Append('\t')
                      .Append(i.SizeBytes).Append('\t').Append(i.Sha256).Append('\t')
                      .Append($"{Base(ctx)}/api/packages/{i.Id}/{i.Version}/download").Append('\t')
                      .Append(i.ZxtName).Append('\t').Append(i.Category).Append('\n');
                }
                return Results.Text(sb.ToString(), "text/tab-separated-values; charset=utf-8");
            }

            var entries = await CatalogAsync(db, beta, Base(ctx));
            return Results.Json(new { ok = true, data = new { channel = beta ? "beta" : "live", packages = entries } });
        });

        api.MapGet("/packages/{id}", async (string id, HttpContext ctx, AppDbContext db, UserManager<AppUser> users) =>
        {
            var package = await db.Packages.Include(p => p.Owner)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (package is null) return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");

            var user = await TryUserAsync(ctx, users);
            var isOwner = user is not null &&
                (package.OwnerId == user.Id || ctx.User.IsInRole("Admin"));

            var versions = await db.PackageVersions.Where(v => v.PackageId == id)
                .OrderByDescending(v => v.SubmittedAt).ToListAsync();
            if (!isOwner)
                versions = versions.Where(v => v.Status is VersionStatus.Live or VersionStatus.Beta).ToList();

            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    id = package.Id,
                    owner = package.Owner?.DisplayName,
                    versions = versions.Select(v => new
                    {
                        v.Version,
                        status = v.Status.ToString().ToLowerInvariant(),
                        v.Changelog,
                        v.SubmittedAt,
                        v.Downloads,
                        reviewComment = isOwner ? v.ReviewComment : null,
                        findings = isOwner ? JsonSerializer.Deserialize<JsonElement>(v.ValidationReportJson) : (object?)null
                    })
                }
            });
        });

        api.MapPost("/packages/validate", async (HttpContext ctx, UserManager<AppUser> users, SubmissionService svc) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            try
            {
                var report = await svc.ValidateOnlyAsync(tmp, user);
                return Results.Json(new { ok = true, findings = report.Findings, data = new { passed = report.Passed } });
            }
            finally { TryDelete(tmp); }
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/packages", async (HttpContext ctx, UserManager<AppUser> users, SubmissionService svc) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            try
            {
                var via = ctx.User.FindFirstValue("token_name") is { } t ? $"api:{t}" : "web";
                var result = await svc.SubmitAsync(tmp, user, via);

                if (result.ErrorCode == "VERSION_EXISTS")
                    return Results.Json(new
                    {
                        ok = false,
                        error = new { code = result.ErrorCode, message = "This version already exists.", hint = result.ErrorHint },
                        findings = result.Report.Findings
                    }, statusCode: 409);

                if (result.Version is null)
                    return Results.Json(new
                    {
                        ok = false,
                        error = new
                        {
                            code = "VALIDATION_FAILED",
                            message = "The package did not pass the automatic checks; nothing was stored.",
                            hint = "Fix every finding with severity 'error' (each carries a concrete hint), then upload again. Use POST /api/packages/validate for dry runs."
                        },
                        findings = result.Report.Findings
                    }, statusCode: 422);

                var v = result.Version;
                return Results.Json(new
                {
                    ok = true,
                    findings = result.Report.Findings,
                    data = new
                    {
                        id = v.PackageId,
                        version = v.Version,
                        status = "beta",
                        sha256 = v.Sha256,
                        sizeBytes = v.SizeBytes,
                        downloadUrl = $"{Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/download",
                        next = "The version is in the beta channel now (visible to clients with the beta option). An admin reviews it for the live store; you will be notified by email. Check GET /api/packages/" + v.PackageId + " for status."
                    }
                }, statusCode: 201);
            }
            finally { TryDelete(tmp); }
        }).RequireAuthorization("BearerOnly");

        api.MapDelete("/packages/{id}/{version}", async (string id, string version, HttpContext ctx,
            UserManager<AppUser> users, AppDbContext db, AuditService audit) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var v = await db.PackageVersions.Include(x => x.Package)
                .FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No version {version} of '{id}'.");

            var isAdmin = ctx.User.IsInRole("Admin");
            if (v.Package!.OwnerId != user.Id && !isAdmin)
                return Results.Json(new { ok = false, error = new { code = "NOT_OWNER", message = "This package belongs to another user.", hint = "Only the owner or an admin can withdraw a version." } }, statusCode: 403);
            if (v.Status == VersionStatus.Live && !isAdmin)
                return Results.Json(new { ok = false, error = new { code = "LIVE_VERSION", message = "Live versions can only be withdrawn by an admin.", hint = "Ask an admin, or submit a higher fixed version instead." } }, statusCode: 403);

            v.Status = VersionStatus.Withdrawn;
            await db.SaveChangesAsync();
            await audit.LogAsync(user.DisplayName, "version.withdrawn", $"{id} {version}", $"previous status reverted by {(isAdmin ? "admin" : "owner")}");
            return Results.Json(new { ok = true, data = new { id, version, status = "withdrawn" } });
        }).RequireAuthorization("BearerOnly");

        api.MapGet("/packages/{id}/{version}/download", async (string id, string version,
            AppDbContext db, SubmissionService svc) =>
        {
            var v = await db.PackageVersions.FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version &&
                (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta));
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No downloadable version {version} of '{id}'.");
            var path = Path.Combine(svc.StorageRoot, v.FilePath);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The package file is missing on the server; contact an admin.");
            v.Downloads++;
            await db.SaveChangesAsync();
            return Results.File(path, "application/zip", $"{id}-{version}.ppak");
        });

        api.MapGet("/devkit", (IConfiguration config, IWebHostEnvironment env, HttpContext ctx) =>
        {
            var root = DevkitRoot(config, env);
            var files = Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f).ToArray()
                : Array.Empty<string>();
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    files,
                    hint = files.Length == 0
                        ? "The developer kit is empty on this instance; an admin can add SDK docs, knowledge files and templates."
                        : "Fetch a file via GET /api/devkit/{path}."
                }
            });
        });

        api.MapGet("/devkit/{**path}", (string path, IConfiguration config, IWebHostEnvironment env) =>
        {
            var root = DevkitRoot(config, env);
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!IsUnder(root, full) || !File.Exists(full))
                return NotFound("FILE_NOT_FOUND", $"No devkit file '{path}'. List files via GET /api/devkit.");
            var ext = Path.GetExtension(full).ToLowerInvariant();
            var type = ext switch
            {
                ".md" => "text/markdown; charset=utf-8",
                ".json" => "application/json",
                ".zip" or ".ppak" => "application/zip",
                ".png" => "image/png",
                _ => "application/octet-stream"
            };
            return Results.File(full, type, Path.GetFileName(full));
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static string Base(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}";

    /// <summary>Maps Power PDF's 3-letter resource codes (DEU, FRA, ...) to two-letter culture names.</summary>
    private static string MapHostLang(string code) => code.ToUpperInvariant() switch
    {
        "DEU" or "GER" => "de",
        "FRA" or "FRE" => "fr",
        "ITA" => "it",
        "ESP" or "SPA" => "es",
        "NLD" or "DUT" => "nl",
        "PTB" or "POR" => "pt",
        "DAN" => "da",
        "FIN" => "fi",
        "NOR" => "nb",
        "SVE" or "SWE" => "sv",
        "PLK" or "POL" => "pl",
        "CSY" or "CZE" => "cs",
        "HUN" => "hu",
        "RUS" => "ru",
        "TRK" or "TUR" => "tr",
        _ => "en"
    };

    private static string DevkitRoot(IConfiguration config, IWebHostEnvironment env)
    {
        var root = config["Storage:Devkit"];
        return string.IsNullOrWhiteSpace(root) ? Path.Combine(env.ContentRootPath, "data", "devkit") : root;
    }

    /// <summary>True when <paramref name="full"/> lies INSIDE root (separator-aware prefix check).</summary>
    private static bool IsUnder(string root, string full)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Unauthorized() => Results.Json(new
    {
        ok = false,
        error = new { code = "UNAUTHENTICATED", message = "Authentication required.", hint = "Send 'Authorization: Bearer ppak_...' with a personal token from your profile page, then call GET /api/me to verify." }
    }, statusCode: 401);

    private static IResult NotFound(string code, string message) => Results.Json(new
    {
        ok = false,
        error = new { code, message, hint = "Check GET /api/catalog for available packages and versions." }
    }, statusCode: 404);

    private static IResult BadUpload() => Results.Json(new
    {
        ok = false,
        error = new
        {
            code = "NO_PACKAGE",
            message = "No package data received.",
            hint = "Send the .ppak either as the raw request body (Content-Type: application/zip) or as multipart/form-data with a file field named 'package'. Example: curl -X POST -H \"Authorization: Bearer ppak_...\" --data-binary @my.ppak -H \"Content-Type: application/zip\" <base>/api/packages/validate"
        }
    }, statusCode: 400);

    /// <summary>Resolves the acting user from either auth scheme; null when unauthenticated or inactive.</summary>
    private static async Task<AppUser?> RequireUserAsync(HttpContext ctx, UserManager<AppUser> users)
    {
        var id = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null) return null;
        var user = await users.FindByIdAsync(id);
        return user is { Status: UserStatus.Active } ? user : null;
    }

    private static async Task<AppUser?> TryUserAsync(HttpContext ctx, UserManager<AppUser> users)
    {
        if (ctx.User.Identity?.IsAuthenticated != true)
        {
            var result = await ctx.AuthenticateAsync(Auth.ApiTokenAuthHandler.Scheme);
            if (result.Succeeded) ctx.User = result.Principal;
        }
        return await RequireUserAsync(ctx, users);
    }

    private static async Task<string?> SaveUploadAsync(HttpRequest request)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ppak-" + Guid.NewGuid().ToString("N") + ".zip");
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files["package"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return null;
            await using var fs = File.Create(tmp);
            await file.CopyToAsync(fs);
            return tmp;
        }
        if (request.ContentLength is null or 0) return null;
        await using (var fs = File.Create(tmp))
            await request.Body.CopyToAsync(fs);
        return new FileInfo(tmp).Length > 0 ? tmp : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp cleanup is best effort */ }
    }

    public static async Task<List<object>> CatalogAsync(AppDbContext db, bool includeBeta, string baseUrl)
    {
        var all = await db.PackageVersions
            .Where(v => v.Status == VersionStatus.Live || (includeBeta && v.Status == VersionStatus.Beta))
            .ToListAsync();
        var cmp = new SemVerComparer();
        var result = new List<object>();
        foreach (var group in all.GroupBy(v => v.PackageId).OrderBy(g => g.Key))
        {
            var live = group.Where(v => v.Status == VersionStatus.Live)
                .OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            var beta = group.Where(v => v.Status == VersionStatus.Beta)
                .OrderByDescending(v => v.Version, cmp).FirstOrDefault();

            var pick = live;
            var channel = "live";
            if (includeBeta && beta is not null && (live is null || cmp.Compare(beta.Version, live.Version) > 0))
            {
                pick = beta;
                channel = "beta";
            }
            if (pick is null) continue;

            using var doc = JsonDocument.Parse(pick.ManifestJson);
            var root = doc.RootElement;
            result.Add(new
            {
                id = pick.PackageId,
                name = CloneOrNull(root, "name"),
                description = CloneOrNull(root, "description"),
                category = root.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String ? cat.GetString() : "other",
                version = pick.Version,
                channel,
                changelog = pick.Changelog,
                minPowerPdfVersion = pick.MinPowerPdfVersion,
                sha256 = pick.Sha256,
                sizeBytes = pick.SizeBytes,
                downloads = pick.Downloads,
                downloadUrl = $"{baseUrl}/api/packages/{pick.PackageId}/{pick.Version}/download"
            });
        }
        return result;
    }

    private static JsonElement? CloneOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.Clone() : null;
}

public record AppVersion(string Value);

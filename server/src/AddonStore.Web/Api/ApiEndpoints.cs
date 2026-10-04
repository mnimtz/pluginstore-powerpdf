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
        // The installer for end users: the MSI inside the newest client package.
        // Stable URL, linked from the landing page; no account needed.
        // Web app manifest for bookmarks, desktop shortcuts and "install as app":
        // Power PDF's own application icon, brand colours, localized name.
        app.MapGet("/site.webmanifest", (Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> L) =>
            Results.Json(new
            {
                name = L["Add-on Store for Tungsten Power PDF"].Value,
                short_name = "Add-on Store",
                description = L["Add-ons for Tungsten Power PDF: discover them here and install them directly in Power PDF."].Value,
                lang = System.Globalization.CultureInfo.CurrentUICulture.Name,
                start_url = "/",
                scope = "/",
                display = "standalone",
                background_color = "#002854",
                theme_color = "#002854",
                icons = new object[]
                {
                    new { src = "/img/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                    new { src = "/img/icon-256.png", sizes = "256x256", type = "image/png", purpose = "any" },
                    new { src = "/img/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" }
                }
            }, contentType: "application/manifest+json"));

        // Install click on a shared add-on page (/a/{slug}); counted per ref, see ShareService.
        app.MapPost("/a/{slug}/click", async (string slug, string? what, HttpContext ctx, AppDbContext db, ShareService share) =>
        {
            if (what != "install") return Results.NoContent();
            var ids = await db.PackageVersions.Where(v => v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)
                .Select(v => v.PackageId).Distinct().ToListAsync();
            var id = ShareService.Resolve(slug, ids);
            if (id is not null) await share.CountAsync(id, ctx.Request.Query["ref"].ToString(), "install");
            return Results.NoContent();
        });

        app.MapGet("/download/pluginstore.msi", async (AppDbContext db, SubmissionService svc, UsageService usage, ShareService share, HttpContext ctx) =>
        {
            var versions = await db.PackageVersions
                .Where(v => v.PackageId == SubmissionService.ClientPackageId &&
                            (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta))
                .ToListAsync();
            var cmp = new SemVerComparer();
            var pick = versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault()
                       ?? versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            if (pick is null) return Results.NotFound();

            var path = Path.Combine(svc.StorageRoot, pick.FilePath);
            if (!File.Exists(path)) return Results.NotFound();

            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var msi = zip.Entries.FirstOrDefault(e =>
                e.FullName.StartsWith("installer/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (msi is null || msi.Length > 100 * 1024 * 1024) return Results.NotFound();

            var ms = new MemoryStream();
            await using (var es = msi.Open()) await es.CopyToAsync(ms);
            ms.Position = 0;
            pick.Downloads++;
            await db.SaveChangesAsync();
            await usage.CountAsync(ctx, "msi", pick.PackageId, pick.Version);
            // Client installer fetched from a shared add-on page: attribute it to that add-on and ref.
            var fromPkg = ctx.Request.Query["pkg"].ToString();
            if (fromPkg.Length > 0 && await db.Packages.AnyAsync(p => p.Id == fromPkg))
                await share.CountAsync(fromPkg, ctx.Request.Query["ref"].ToString(), "client");
            return Results.File(ms, "application/x-msi", $"AddonStore-{pick.Version}.msi");
        });

        var api = app.MapGroup("/api");

        api.MapGet("/", (AppVersion ver, HttpContext ctx) => Results.Json(new
        {
            ok = true,
            data = new
            {
                name = "Add-on Store for Tungsten Power PDF",
                version = ver.Value,
                docs = Base(ctx) + "/api/agent-guide",
                endpoints = new[]
                {
                    "GET  /api/agent-guide            full instructions for developers and AI agents (markdown)",
                    "GET  /api/schema/manifest        JSON schema of manifest.json",
                    "GET  /api/skill                  Claude Code skill (SKILL.md) for this store",
                    "GET  /api/me                     verify your token, see your packages (auth)",
                    "GET  /api/catalog?channel=beta   released packages; beta channel includes pre-release versions",
                    "GET  /api/packages/{id}          status and history of one package",
                    "POST /api/packages/validate      dry-run: full validation, nothing stored (auth)",
                    "POST /api/packages               submit a package (auth)",
                    "GET  /api/categories             catalog categories (slug, names, usage, limit)",
                    "PATCH  /api/packages/{id}  change the catalog entry: name, description, author, contactEmail, category (owner/admin, auth)",
                    "PUT  /api/packages/{id}/{version}/source  upload the source code ZIP of a version (owner/admin, auth)",
                    "GET  /api/packages/{id}/{version}/source  download the source code (admins only)",
                    "GET  /api/packages/{id}/source/latest  source code of the newest version that has one (admins only; header X-Source-Version)",
                    "DELETE /api/packages/{id}/{version}  withdraw your own beta version (auth)",
                    "GET  /api/packages/{id}/{version}/download",
                    "GET  /api/packages/{id}/icon     catalog icon (PNG) of the newest released version",
                    "GET  /api/devkit                 SDK documentation and developer kit files"
                }
            }
        }));

        api.MapGet("/ping", (AppVersion ver) => Results.Json(new { ok = true, data = new { name = "pluginstore-powerpdf", version = ver.Value } }));

        api.MapGet("/agent-guide", (HttpContext ctx, AppVersion ver) =>
            Results.Text(AgentGuide.Markdown(Base(ctx), ver.Value), "text/markdown; charset=utf-8"));

        api.MapGet("/schema/manifest", () => Results.Text(AgentGuide.ManifestSchema, "application/json"));

        api.MapGet("/skill", (HttpContext ctx) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(AgentGuide.SkillMarkdown(Base(ctx))),
                         "text/markdown; charset=utf-8", "SKILL.md"));

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

        api.MapGet("/catalog", async (AppDbContext db, HttpContext ctx, UsageService usage, string? channel, string? format, string? lang) =>
        {
            var beta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);

            // TSV variant for native clients (the Power PDF ribbon add-on): one
            // line per package, text fields with tabs/newlines flattened.
            if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
            {
                var culture = (lang ?? "en").Trim();
                if (culture.Length > 2) culture = MapHostLang(culture);
                // Store window opened (or refreshed) in a client: basis of the
                // "clients in use" report; anonymous, see UsageService.
                await usage.CountAsync(ctx, "catalog", lang: culture);
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
                      .Append(i.ZxtName).Append('\t').Append(i.Category).Append('\t')
                      .Append(Flat(i.Author)).Append('\t').Append(Flat(i.ContactEmail)).Append('\t')
                      .Append(Flat(i.CategoryName)).Append('\t')
                      .Append($"{Base(ctx)}/api/packages/{i.Id}/icon").Append('\n');
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
                    catalogEntry = new
                    {
                        name = ParseOrNull(package.NameJson),
                        description = ParseOrNull(package.DescriptionJson),
                        author = package.Author,
                        contactEmail = package.ContactEmail,
                        category = package.CategoryOverride,
                        updatedAt = package.MetaUpdatedAt,
                        updatedBy = package.MetaUpdatedBy,
                        note = "null fields come from the newest manifest; change them with PATCH /api/packages/" + package.Id
                    },
                    versions = versions.Select(v => new
                    {
                        v.Version,
                        status = v.Status.ToString().ToLowerInvariant(),
                        v.Changelog,
                        v.SubmittedAt,
                        v.Downloads,
                        reviewComment = isOwner ? v.ReviewComment : null,
                        hasSource = v.SourcePath is not null,
                        sourceUploadedAt = isOwner ? v.SourceUploadedAt : null,
                        // Admins only: where to fetch it (source round trip, see the agent guide).
                        sourceUrl = v.SourcePath is not null && ctx.User.IsInRole("Admin")
                            ? $"{Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/source" : null,
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

        api.MapPost("/packages", async (HttpContext ctx, UserManager<AppUser> users, SubmissionService svc, SourceService sources) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            try
            {
                var via = ctx.User.FindFirstValue("token_name") is { } t ? $"api:{t}" : "web";
                var result = await svc.SubmitAsync(tmp, user, via);

                if (result.ErrorCode == "CLIENT_ADMIN_ONLY")
                    return Results.Json(new
                    {
                        ok = false,
                        error = new { code = result.ErrorCode, message = "Not allowed.", hint = result.ErrorHint },
                        findings = result.Report.Findings
                    }, statusCode: 403);

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
                var policy = await sources.PolicyAsync();
                return Results.Json(new
                {
                    ok = true,
                    findings = result.Report.Findings,
                    data = new
                    {
                        id = v.PackageId,
                        version = v.Version,
                        status = v.Status.ToString().ToLowerInvariant(),
                        sourcePolicy = policy,
                        sourceUploadUrl = $"{Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/source",
                        sourceNext = policy == "off" ? null
                            : $"Now upload the source code of this version: PUT {Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/source with a ZIP of the source tree (Content-Type: application/zip)."
                              + (policy == "required" && v.Status != VersionStatus.Live ? " Required: an admin cannot approve the version without it." : ""),
                        sha256 = v.Sha256,
                        sizeBytes = v.SizeBytes,
                        downloadUrl = $"{Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/download",
                        next = v.Status == VersionStatus.Live
                            ? "The Add-on Store client version is live immediately; installed clients offer it as an update."
                            : "The version is in the beta channel now (visible to clients with the beta option). An admin reviews it for the live store; you will be notified by email. Check GET /api/packages/" + v.PackageId + " for status."
                    }
                }, statusCode: 201);
            }
            finally { TryDelete(tmp); }
        }).RequireAuthorization("BearerOnly");

        api.MapPut("/packages/{id}/{version}/source", async (string id, string version, HttpContext ctx,
            UserManager<AppUser> users, AppDbContext db, SourceService sources) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var v = await db.PackageVersions.Include(x => x.Package).FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No version {version} of '{id}'. Upload the package first (POST /api/packages).");
            if (v.Package!.OwnerId != user.Id && !ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "NOT_OWNER", message = "This package belongs to another user.", hint = "Only the owner or an admin can upload its source code." } }, statusCode: 403);
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            try
            {
                var report = await sources.UploadAsync(v, tmp, user);
                if (!report.Passed)
                    return Results.Json(new
                    {
                        ok = false,
                        error = new { code = "SOURCE_REJECTED", message = "The source code was not stored.", hint = "Fix every finding with severity 'error' and upload again." },
                        findings = report.Findings
                    }, statusCode: 422);
                return Results.Json(new
                {
                    ok = true,
                    findings = report.Findings,
                    data = new { id, version, sizeBytes = v.SourceSizeBytes, sha256 = v.SourceSha256, next = "Source stored. It is visible to store admins only and is not delivered to Power PDF clients." }
                });
            }
            finally { TryDelete(tmp); }
        }).RequireAuthorization("BearerOnly");

        api.MapGet("/packages/{id}/{version}/source", async (string id, string version, HttpContext ctx,
            AppDbContext db, SourceService sources) =>
        {
            if (!ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "ADMIN_ONLY", message = "Source code is visible to store admins only.", hint = "Use an admin account or an admin's API token." } }, statusCode: 403);
            var v = await db.PackageVersions.FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
            if (v?.SourcePath is null) return NotFound("SOURCE_MISSING", $"No source code stored for {id} {version}.");
            var path = sources.FullPath(v);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The source file is missing on the server; restore it from a backup.");
            return Results.File(path, "application/zip", $"{id}-{version}-source.zip");
        }).RequireAuthorization("ApiOrCookie");

        // Source of the newest version that has one: the starting point when an
        // admin (or their Claude session) changes an existing add-on.
        api.MapGet("/packages/{id}/source/latest", async (string id, HttpContext ctx, AppDbContext db, SourceService sources) =>
        {
            if (!ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "ADMIN_ONLY", message = "Source code is visible to store admins only.", hint = "Use an admin account or an admin's API token." } }, statusCode: 403);
            var withSource = await db.PackageVersions.Where(x => x.PackageId == id && x.SourcePath != null).ToListAsync();
            var v = withSource.OrderByDescending(x => x.Version, new SemVerComparer()).FirstOrDefault();
            if (v is null) return NotFound("SOURCE_MISSING", $"No source code stored for any version of '{id}'.");
            var path = sources.FullPath(v);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The source file is missing on the server; restore it from a backup.");
            ctx.Response.Headers["X-Source-Version"] = v.Version;
            return Results.File(path, "application/zip", $"{id}-{v.Version}-source.zip");
        }).RequireAuthorization("ApiOrCookie");

        // Catalog icon (assets/icon.png of the newest live, else beta, version); public like the catalog.
        api.MapGet("/packages/{id}/icon", async (string id, AppDbContext db, SubmissionService svc) =>
        {
            var cmp = new SemVerComparer();
            var versions = await db.PackageVersions
                .Where(v => v.PackageId == id && (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)).ToListAsync();
            var pick = versions.Where(v => v.Status == VersionStatus.Live).OrderByDescending(v => v.Version, cmp).FirstOrDefault()
                       ?? versions.OrderByDescending(v => v.Version, cmp).FirstOrDefault();
            if (pick is null) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var path = Path.Combine(svc.StorageRoot, pick.FilePath);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The package file is missing on the server.");
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                var entry = zip.GetEntry("assets/icon.png");
                if (entry is null || entry.Length > 4 * 1024 * 1024) return NotFound("ICON_MISSING", "This package has no icon.");
                using var s = entry.Open();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P') return NotFound("ICON_MISSING", "This package has no valid PNG icon.");
                return Results.File(bytes, "image/png", lastModified: pick.SubmittedAt, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{pick.Sha256[..16]}\""));
            }
            catch (InvalidDataException) { return NotFound("ICON_MISSING", "The package could not be read."); }
        });

        api.MapGet("/categories", async (AppDbContext db, CategoryService categories) =>
        {
            var all = await categories.AllAsync();
            var pkgs = await db.Packages.Include(p => p.Versions).ToListAsync();
            var used = pkgs.GroupBy(CategoryService.EffectiveSlug).ToDictionary(g => g.Key, g => g.Count());
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    limit = await categories.MaxAsync(),
                    categories = all.Select(c => new
                    {
                        slug = c.Slug,
                        name = ParseOrNull(c.NameJson),
                        builtin = c.Builtin,
                        packages = used.GetValueOrDefault(c.Slug)
                    }),
                    hint = "Use an existing slug as manifest 'category'. Propose a new one only if none fits (see the agent guide, section Categories)."
                }
            });
        });

        api.MapPatch("/packages/{id}", async (string id, HttpContext ctx, UserManager<AppUser> users,
            AppDbContext db, PackageMetaService meta) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var pkg = await db.Packages.FirstOrDefaultAsync(p => p.Id == id);
            if (pkg is null) return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");
            if (pkg.OwnerId != user.Id && !ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "NOT_OWNER", message = "This package belongs to another user.", hint = "Only the owner or an admin can change the catalog entry." } }, statusCode: 403);

            JsonElement body;
            try
            {
                if (ctx.Request.ContentLength is > 256 * 1024) throw new JsonException();
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                body = doc.RootElement.Clone();
                if (body.ValueKind != JsonValueKind.Object) throw new JsonException();
            }
            catch (JsonException)
            {
                return Results.Json(new { ok = false, error = new { code = "METADATA_INVALID", message = "The body must be a JSON object (max. 256 KB).",
                    hint = "Example: {\"author\": \"Team Signing\", \"contactEmail\": \"team@example.com\", \"name\": {\"en\": \"...\"}, \"description\": {\"en\": \"...\", ...all 16 languages}}. Omit a field to keep it, send null to reset it to the manifest value." } }, statusCode: 400);
            }

            var change = new MetaChange();
            Dictionary<string, string>? Map(JsonElement el) => el.ValueKind == JsonValueKind.Object
                ? el.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                : null;
            var typeErrors = new List<MetaIssue>();
            foreach (var prop in body.EnumerateObject())
            {
                var v = prop.Value;
                switch (prop.Name)
                {
                    case "name":
                        change.SetName = true; change.Name = v.ValueKind == JsonValueKind.Null ? null : Map(v);
                        if (v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) typeErrors.Add(new("NAME_INVALID", "error", "'name' must be an object or null.", "Example: {\"en\": \"My Plugin\"}."));
                        break;
                    case "description":
                        change.SetDescription = true; change.Description = v.ValueKind == JsonValueKind.Null ? null : Map(v);
                        if (v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) typeErrors.Add(new("LANG_TEXT_INCOMPLETE", "error", "'description' must be an object with all 16 languages, or null.", "Example: {\"en\": \"...\", \"de\": \"...\", ...}."));
                        break;
                    case "author":
                        change.SetAuthor = true; change.Author = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        break;
                    case "contactEmail":
                        change.SetContact = true; change.ContactEmail = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        break;
                    case "category":
                        change.SetCategory = true; change.Category = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        break;
                    default:
                        typeErrors.Add(new("METADATA_INVALID", "error", $"Unknown field '{prop.Name}'.", "Allowed fields: name, description, author, contactEmail, category."));
                        break;
                }
            }

            var issues = typeErrors.Count > 0 ? typeErrors : await meta.ApplyAsync(pkg, user, change);
            var findings = issues.Select(i => new { code = i.Code, severity = i.Severity, message = i.Message, hint = i.Hint });
            if (issues.Any(i => i.Severity == "error"))
                return Results.Json(new { ok = false, error = new { code = "METADATA_INVALID", message = "The catalog entry was not changed.", hint = "Fix every finding with severity 'error' and send the request again." }, findings }, statusCode: 422);
            return Results.Json(new
            {
                ok = true,
                findings,
                data = new
                {
                    id = pkg.Id,
                    name = ParseOrNull(pkg.NameJson), description = ParseOrNull(pkg.DescriptionJson),
                    author = pkg.Author, contactEmail = pkg.ContactEmail, category = pkg.CategoryOverride,
                    updatedAt = pkg.MetaUpdatedAt, updatedBy = pkg.MetaUpdatedBy,
                    next = "The catalog, the web UI and the Power PDF client show the new values immediately; no new version is needed."
                }
            });
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
            AppDbContext db, SubmissionService svc, UsageService usage, HttpContext ctx) =>
        {
            var v = await db.PackageVersions.FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version &&
                (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta));
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No downloadable version {version} of '{id}'.");
            var path = Path.Combine(svc.StorageRoot, v.FilePath);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The package file is missing on the server; contact an admin.");
            v.Downloads++;
            await db.SaveChangesAsync();
            await usage.CountAsync(ctx, "download", v.PackageId, v.Version);
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
        var ownerRows = await db.Packages.Include(p => p.Owner).ToListAsync();
        var owners = ownerRows.ToDictionary(p => p.Id, p => Services.CatalogUi.PublicName(p.Owner));
        var ownerMails = ownerRows.ToDictionary(p => p.Id, p => Services.CatalogUi.PublicEmail(p.Owner));
        var pkgs = ownerRows.ToDictionary(p => p.Id);
        var known = await db.Categories.ToDictionaryAsync(c => c.Slug);
        // Share page slugs over live and beta ids, the same set /a/{slug} resolves against.
        var slugIds = (await db.PackageVersions.Where(v => v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)
            .Select(v => v.PackageId).Distinct().ToListAsync()).Where(i => i != SubmissionService.ClientPackageId).ToList();
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
            var pkg = pkgs.GetValueOrDefault(pick.PackageId);
            result.Add(new
            {
                id = pick.PackageId,
                name = ParseOrNull(pkg?.NameJson) ?? CloneOrNull(root, "name"),
                description = ParseOrNull(pkg?.DescriptionJson) ?? CloneOrNull(root, "description"),
                category = Services.CatalogUi.EffectiveCategory(pkg, root, known),
                author = Services.CatalogUi.EffectiveAuthor(pkg, root, owners.GetValueOrDefault(pick.PackageId, pick.SubmittedBy)),
                contactEmail = Services.CatalogUi.EffectiveContact(pkg, root, ownerMails.GetValueOrDefault(pick.PackageId, "")),
                version = pick.Version,
                channel,
                changelog = pick.Changelog,
                minPowerPdfVersion = pick.MinPowerPdfVersion,
                sha256 = pick.Sha256,
                sizeBytes = pick.SizeBytes,
                downloads = pick.Downloads,
                pageUrl = pick.PackageId == SubmissionService.ClientPackageId ? null
                    : $"{baseUrl}/a/{ShareService.Slug(pick.PackageId, slugIds)}",
                downloadUrl = $"{baseUrl}/api/packages/{pick.PackageId}/{pick.Version}/download"
            });
        }
        return result;
    }

    private static JsonElement? CloneOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.Clone() : null;

    private static JsonElement? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
}

public record AppVersion(string Value);

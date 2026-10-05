using System.Security.Claims;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using AddonStore.Web.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
            var ids = (await db.PackageVersions.Where(v => v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)
                .Where(v => db.Packages.Any(p => p.Id == v.PackageId && p.Visibility != "private"))
                .Select(v => v.PackageId).Distinct().ToListAsync()).Where(i => i != SubmissionService.ClientPackageId).ToList();
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

            // extracted once per package version into a disk cache, then streamed (no 100 MB buffers per request)
            var cacheDir = Path.Combine(Path.GetTempPath(), "addonstore-msi");
            Directory.CreateDirectory(cacheDir);
            var cached = Path.Combine(cacheDir, pick.Sha256[..32] + ".msi");
            if (!File.Exists(cached))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                var msi = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("installer/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
                if (msi is null || msi.Length > 100 * 1024 * 1024) return Results.NotFound();
                var part = cached + "." + Guid.NewGuid().ToString("N") + ".part";
                await using (var es = msi.Open())
                await using (var fs = File.Create(part))
                    await es.CopyToAsync(fs);
                try { File.Move(part, cached, overwrite: true); } catch (IOException) { TryDelete(part); }
            }
            await db.PackageVersions.Where(x => x.Id == pick.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Downloads, x => x.Downloads + 1));
            await usage.CountAsync(ctx, "msi", pick.PackageId, pick.Version);
            // Client installer fetched from a shared add-on page: attribute it to that add-on and ref.
            var fromPkg = ctx.Request.Query["pkg"].ToString();
            if (fromPkg.Length > 0 && await db.Packages.AnyAsync(p => p.Id == fromPkg))
                await share.CountAsync(fromPkg, ctx.Request.Query["ref"].ToString(), "client");
            return Results.File(cached, "application/x-msi", $"AddonStore-{pick.Version}.msi");
        });

        // llms.txt (llmstxt.org): entry point for any language model or AI assistant.
        app.MapGet("/llms.txt", (HttpContext ctx, AppVersion ver) => MarkdownText(ctx, AgentGuide.LlmsTxt(Base(ctx), ver.Value)));

        // The agent guide as a plain HTML page (S1.0.4): the web readers of some
        // assistants (ChatGPT, Gemini) refuse text/markdown but read any web page.
        // short excerpts as web pages too (S1.0.8)
        app.MapGet("/agent-guide/checklist", (HttpContext ctx, AppVersion ver) =>
            Results.Content(GuideHtml("Pre-flight checklist - Add-on Store for Tungsten Power PDF", AgentGuide.Checklist(Base(ctx), ver.Value)), "text/html; charset=utf-8"));
        app.MapGet("/agent-guide/manual-upload", (HttpContext ctx, AppVersion ver) =>
            Results.Content(GuideHtml("Manual upload package - Add-on Store for Tungsten Power PDF", AgentGuide.ManualUpload(Base(ctx), ver.Value)), "text/html; charset=utf-8"));

        app.MapGet("/agent-guide", (HttpContext ctx, AppVersion ver) =>
        {
            var md = AgentGuide.Markdown(Base(ctx), ver.Value);
            var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                       "<title>Agent guide - Add-on Store for Tungsten Power PDF</title>" +
                       "<meta name=\"description\" content=\"How an AI assistant publishes, updates and maintains Power PDF add-ons through the Add-on Store API.\"></head>" +
                       "<body style=\"font-family:Arial,sans-serif;max-width:980px;margin:24px auto;padding:0 16px;color:#002854\">" +
                       "<p>Machine-readable versions: <a href=\"/api/agent-guide\">/api/agent-guide</a> (Markdown), " +
                       "<a href=\"/api/openapi.json\">/api/openapi.json</a>, <a href=\"/llms.txt\">/llms.txt</a>.</p>" +
                       "<pre style=\"white-space:pre-wrap;word-wrap:break-word;font:14px/1.5 Consolas,monospace\">" +
                       System.Net.WebUtility.HtmlEncode(md) + "</pre></body></html>";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // robots.txt: public pages and every documentation entry point may be read by anyone.
        app.MapGet("/robots.txt", () => Results.Text(
            """
            User-agent: *
            Allow: /
            Allow: /llms.txt
            Allow: /agent-guide
            Allow: /api/agent-guide
            Allow: /api/openapi.json
            Disallow: /Admin/
            Disallow: /Account/
            Disallow: /Dashboard
            Disallow: /Profile
            Disallow: /Customer
            Disallow: /Customers
            """, "text/plain; charset=utf-8"));

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
                    "GET  /api/openapi.json           OpenAPI 3.1 description of this API (ChatGPT actions, Gemini function calling, tools)",
                    "GET  /api/agents-md              AGENTS.md for coding assistants (Codex, Copilot, Cursor, Gemini CLI)",
                    "GET  /api/skill                  Claude Code skill (SKILL.md) for this store",
                    "GET  /llms.txt                   short index of this site for language models",
                    "GET  /api/me                     verify your token, see your packages (auth)",
                    "GET  /api/catalog?channel=beta   released packages; beta channel includes pre-release versions",
                    "GET  /api/packages/{id}          status and history of one package",
                    "POST /api/packages/validate      dry-run: full validation, nothing stored (auth)",
                    "POST /api/packages               submit a package (auth)",
                    "GET  /api/categories             catalog categories (slug, names, usage, limit)",
                    "PATCH  /api/packages/{id}  change the catalog entry: name, description, author, contactEmail, category, visibility (owner/admin, auth)",
                    "PUT  /api/packages/{id}/{version}/source  upload the source code ZIP of a version (owner/admin, auth)",
                    "GET  /api/packages/{id}/{version}/source  download the source code (admins only)",
                    "GET  /api/packages/{id}/source/latest  source code of the newest version that has one (admins only; header X-Source-Version)",
                    "GET  /api/packages/{id}/screenshots  screenshot list with captions (?lang=, ?format=tsv); /screenshots/{n} the image",
                    "POST /api/packages/{id}/rating  {installId, stars 1-5, version} from the store client (anonymous)",
                    "POST /api/packages/{id}/feedback  {installId, kind problem|comment, message, email?, version, log?} from the store client",
                    "GET  /api/packages/{id}/feedback  ratings and feedback of your package (owner/admin, ?status=open|done)",
                    "PATCH /api/packages/{id}/feedback/{fid}  {status: open|done} (owner/admin)",
                    "GET  /api/features               which optional AI features are switched on",
                    "GET  /api/search?q=&lang=&channel=  find add-ons by need (AI ranking with reasons when enabled, else word search; ?format=tsv)",
                    "GET  /api/signing-key              public key of the catalog signatures (ECDSA P-256)",
                    "GET|POST /api/packages/{id}/{version}/ai-review  read or create the AI review aid (reviewers/admins)",
                    "GET|POST /api/customers          customer deliveries: list or create customers (auth)",
                    "GET|PATCH /api/customers/{cid}   one customer with codes and deliveries (creator/admin; reviewers read); DELETE removes it with its codes and deliveries",
                    "POST /api/customers/{cid}/codes  new code for the customer or one delivery {deliveryId?, transitionDays?}; DELETE .../codes/{codeId} revokes",
                    "POST /api/customers/{cid}/deliveries  deliver an add-on {packageId, beta{mode,version}, live{mode,version}, startsAt?, endsAt?, ownCode?}",
                    "PATCH /api/deliveries/{did}       change stages, dates or status; POST /api/deliveries/{did}/promote = beta version goes live",
                    "DELETE /api/packages/{id}/{version}  withdraw your own beta version (auth)",
                    "GET  /api/packages/{id}/{version}/download",
                    "GET  /api/customer-code          check the customer code in the X-Customer-Code header (valid, customer, add-ons)",
                    "GET  /api/packages/{id}/icon[?v=version]  catalog icon (PNG), of the given or the newest released version",
                    "GET  /api/devkit                 SDK documentation and developer kit files",
                    "GET  /api/agent-guide/checklist  every hard rule on one short page (HTML: /agent-guide/checklist)",
                    "GET  /api/rules                  every rule grouped by area, with the store's own rules (house rules, stricter warnings)",
                    "GET  /api/agent-guide/manual-upload  package format, step-by-step creation and the one-file manual upload",
                    "GET  /api/tools/make-ppak.ps1    offline packer: .ppak and the upload package (.ppak + source ZIP) for a manual upload on the website"
                }
            }
        }));

        api.MapGet("/ping", (AppVersion ver) => Results.Json(new { ok = true, data = new { name = "pluginstore-powerpdf", version = ver.Value } }));

        // "?download=1" saves it as a file, for assistants without network access (S1.0.7)
        api.MapGet("/agent-guide", (HttpContext ctx, AppVersion ver) => ctx.Request.Query.ContainsKey("download")
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(AgentGuide.Markdown(Base(ctx), ver.Value).Replace("\r\n", "\n")), "text/markdown; charset=utf-8", "AGENT-GUIDE.md")
            : MarkdownText(ctx, AgentGuide.Markdown(Base(ctx), ver.Value)));

        api.MapGet("/agent-guide/checklist", (HttpContext ctx, AppVersion ver) => MarkdownText(ctx, AgentGuide.Checklist(Base(ctx), ver.Value)));
        api.MapGet("/agent-guide/manual-upload", (HttpContext ctx, AppVersion ver) => MarkdownText(ctx, AgentGuide.ManualUpload(Base(ctx), ver.Value)));

        api.MapGet("/schema/manifest", () => Results.Text(AgentGuide.ManifestSchema, "application/json"));

        // Every rule grouped by area, with the store's own rules (S1.0.9).
        api.MapGet("/rules", () =>
        {
            var house = AddonStore.Web.Validation.RuleCatalog.House;
            var rules = AddonStore.Web.Validation.RuleCatalog.Rules;
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    areas = AddonStore.Web.Validation.RuleCatalog.Areas.Select(a => new
                    {
                        name = a,
                        rules = rules.Where(r => r.Area == a).Select(r => new
                        {
                            code = r.Code,
                            severity = r.Severity == "warning" && house.Escalated.Contains(r.Code) ? "error" : r.Severity,
                            kind = r.Api ? "api" : "package",
                            stricterHere = house.Escalated.Contains(r.Code),
                            description = r.Description
                        }),
                        houseRules = house.Rules.Where(h => h.Area == a).Select(h => new { id = h.Id, title = h.Title, text = h.Text, kind = h.Kind })
                    }),
                    escalated = house.Escalated,
                    counts = new
                    {
                        rules = rules.Count,
                        errors = rules.Count(r => !r.Api && (r.Severity == "error" || house.Escalated.Contains(r.Code))),
                        warnings = rules.Count(r => !r.Api && r.Severity == "warning" && !house.Escalated.Contains(r.Code)),
                        info = rules.Count(r => !r.Api && r.Severity == "info"),
                        api = rules.Count(r => r.Api),
                        houseRules = house.Rules.Count
                    }
                }
            });
        });

        // Vendor-neutral entry points (S0.13.0): OpenAPI for tools and function calling, AGENTS.md for coding assistants.
        api.MapGet("/openapi.json", (HttpContext ctx, AppVersion ver) =>
            Results.Text(OpenApiDoc.Json(Base(ctx), ver.Value), "application/json; charset=utf-8"));

        // Shown inline (readable by web readers); "?download=1" saves it as a file.
        api.MapGet("/agents-md", (HttpContext ctx) => ctx.Request.Query.ContainsKey("download")
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(AgentGuide.AgentsMarkdown(Base(ctx)).Replace("\r\n", "\n")), "text/markdown; charset=utf-8", "AGENTS.md")
            : MarkdownText(ctx, AgentGuide.AgentsMarkdown(Base(ctx))));

        api.MapGet("/skill", (HttpContext ctx) => ctx.Request.Query.ContainsKey("download")
            ? Results.File(System.Text.Encoding.UTF8.GetBytes(AgentGuide.SkillMarkdown(Base(ctx)).Replace("\r\n", "\n")), "text/markdown; charset=utf-8", "SKILL.md")
            : MarkdownText(ctx, AgentGuide.SkillMarkdown(Base(ctx))));

        // Offline packer (S1.0.7): builds the .ppak and the upload package (.ppak + source ZIP).
        // Shown inline as text for web readers; "?download=1" saves it as a file.
        api.MapGet("/tools/make-ppak.ps1", (HttpContext ctx) => ctx.Request.Query.ContainsKey("download")
            ? Results.File(System.Text.Encoding.ASCII.GetBytes(PackTool.Script.Replace("\r\n", "\n").Replace("\n", "\r\n")),
                           "text/plain; charset=us-ascii", PackTool.FileName)
            : Results.Text(PackTool.Script.Replace("\r\n", "\n"), "text/plain; charset=utf-8"));

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

        // Public key the catalog signatures can be checked with (clients pin it).
        api.MapGet("/signing-key", async (PackageSigning signing) =>
        {
            await signing.EnsureLoadedAsync();
            return Results.Json(new { ok = true, data = new {
                keyId = signing.KeyId, algorithm = PackageSigning.Algorithm, publicKeyPem = signing.PublicKeyPem,
                publicKeyRaw = signing.PublicKeyRaw,
                message = PackageSigning.MessagePrefix + "\\n{id}\\n{version}\\n{sha256 lowercase hex}\\n{zxtName}",
                available = signing.Problem is null,
                signatureFormat = "keyId:base64(r||s), IEEE P1363, in the catalog (TSV column 21, JSON field signature)" } });
        });

        api.MapGet("/catalog", async (AppDbContext db, HttpContext ctx, UsageService usage, CustomerService customers,
                                      PackageSigning signing, string? channel, string? format, string? lang) =>
        {
            var beta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);
            await signing.EnsureLoadedAsync();
            // Customer codes in the X-Customer-Code header unlock delivered add-ons (S0.14.0).
            var grants = await customers.GrantsAsync(ctx);

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
                if (grants.Count > 0)
                {
                    var cctx = await Services.CatalogUi.Context.LoadAsync(db);
                    var deliveredIds = new HashSet<string>();
                    foreach (var g in grants)
                    {
                        var (v, ch) = CustomerService.Pick(g, beta);
                        if (v is null || !deliveredIds.Add(v.PackageId)) continue;
                        items.RemoveAll(i => i.Id == v.PackageId);   // a delivery overrides the public entry
                        items.Add(Services.CatalogUi.Item(cctx, v, ch, culture, g.Customer.Name));
                    }
                }
                static string Flat(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                var sb = new System.Text.StringBuilder();
                foreach (var i in items)
                {
                    sb.Append(Flat(i.Id)).Append('\t').Append(Flat(i.Version)).Append('\t').Append(Flat(i.Channel)).Append('\t')
                      .Append(Flat(i.Name)).Append('\t').Append(Flat(i.Description)).Append('\t')
                      .Append(Flat(i.Changelog)).Append('\t').Append(Flat(i.MinHost)).Append('\t')
                      .Append(i.SizeBytes).Append('\t').Append(i.Sha256).Append('\t')
                      .Append($"{Base(ctx)}/api/packages/{i.Id}/{i.Version}/download").Append('\t')
                      .Append(Flat(i.ZxtName)).Append('\t').Append(Flat(i.Category)).Append('\t')
                      .Append(Flat(i.Author)).Append('\t').Append(Flat(i.ContactEmail)).Append('\t')
                      .Append(Flat(i.CategoryName)).Append('\t')
                      .Append($"{Base(ctx)}/api/packages/{i.Id}/icon?v={Uri.EscapeDataString(i.Version)}").Append('\t')
                      .Append(i.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                      .Append(i.RatingCount).Append('\t').Append(i.Screenshots).Append('\t')
                      .Append(Flat(i.Customer)).Append('\t')
                      .Append(signing.Sign(i.Id, i.Version, i.Sha256, i.ZxtName)).Append('\n');
                }
                return Results.Text(sb.ToString(), "text/tab-separated-values; charset=utf-8");
            }

            var entries = await CatalogAsync(db, beta, Base(ctx), grants, signing);
            return Results.Json(new { ok = true, data = new { channel = beta ? "beta" : "live", packages = entries } });
        });

        api.MapGet("/packages/{id}", async (string id, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, CustomerService customers) =>
        {
            var package = await db.Packages.Include(p => p.Owner)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (package is null) return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");
            if (package.Visibility == "private")
            {
                // details (all versions, changelogs, owner) are for the people who manage it,
                // not for customers: they see what was delivered in the catalog
                var u = await TryUserAsync(ctx, users);
                if (u is null || !(package.OwnerId == u.Id || ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Reviewer")))
                    return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");
            }

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
                    owner = isOwner ? package.Owner?.DisplayName : Services.CatalogUi.PublicName(package.Owner),
                    visibility = package.Visibility == "private" ? "private" : "public",
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

        api.MapPost("/packages/validate", async (HttpContext ctx, UserManager<AppUser> users, SubmissionService svc, IMemoryCache cache) =>
        {
            if (TooManyUploads(ctx, cache)) return Results.Json(new { ok = false, error = new { code = "RATE_LIMITED",
                message = "Too many package checks from this account in the last hour.", hint = "Wait a while; at most 60 checks and submissions per account and hour." } }, statusCode: 429);
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            UploadBundle.Result? bundle = null;
            try
            {
                try { bundle = UploadBundle.TryUnpack(tmp); }
                catch (InvalidDataException ex) { return BundleInvalid(ex.Message); }
                var report = await svc.ValidateOnlyAsync(bundle?.PpakPath ?? tmp, user);
                return Results.Json(new { ok = true, findings = report.Findings, data = new
                {
                    passed = report.Passed,
                    uploadPackage = bundle is not null,
                    sourceIncluded = bundle?.SourcePath is not null
                } });
            }
            finally { bundle?.Dispose(); TryDelete(tmp); }
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/packages", async (HttpContext ctx, UserManager<AppUser> users, SubmissionService svc, SourceService sources, IMemoryCache cache) =>
        {
            if (TooManyUploads(ctx, cache)) return Results.Json(new { ok = false, error = new { code = "RATE_LIMITED",
                message = "Too many package checks from this account in the last hour.", hint = "Wait a while; at most 60 checks and submissions per account and hour." } }, statusCode: 429);
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var tmp = await SaveUploadAsync(ctx.Request);
            if (tmp is null) return BadUpload();
            UploadBundle.Result? bundle = null;
            try
            {
                // upload package (S1.0.7): .ppak + source ZIP of the same version in one file
                try { bundle = UploadBundle.TryUnpack(tmp); }
                catch (InvalidDataException ex) { return BundleInvalid(ex.Message); }
                var via = ctx.User.FindFirstValue("token_name") is { } t ? $"api:{t}" : "web";
                var result = await svc.SubmitAsync(bundle?.PpakPath ?? tmp, user, via);

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
                ValidationReport? sourceReport = bundle?.SourcePath is { } sp
                    ? await sources.UploadAsync(v, sp, user, ctx.User.IsInRole("Admin")) : null;
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
                        // set for an upload package with a source ZIP: stored at this version, or why not
                        source = sourceReport is null ? null : new { stored = sourceReport.Passed, findings = sourceReport.Findings },
                        sourceUploadUrl = $"{Base(ctx)}/api/packages/{v.PackageId}/{v.Version}/source",
                        sourceNext = policy == "off" || sourceReport?.Passed == true ? null
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
            finally { bundle?.Dispose(); TryDelete(tmp); }
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
                var report = await sources.UploadAsync(v, tmp, user, ctx.User.IsInRole("Admin"));
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

        // Customer code check for the store window (S0.19.0): code in the X-Customer-Code header,
        // never in the URL. Anonymous like the catalog; unknown codes count against the limit.
        api.MapGet("/customer-code", async (HttpContext ctx, CustomerService customers) =>
        {
            if (string.IsNullOrWhiteSpace(ctx.Request.Headers[CustomerService.HeaderName].ToString()))
                return Results.Json(new { ok = false, error = new { code = "CODE_MISSING", message = "Send the customer code in the X-Customer-Code header.", hint = "The code never goes into the URL." } }, statusCode: 400);
            var (valid, customer, addons) = await customers.CheckAsync(ctx);
            return Results.Json(new { ok = true, data = new { valid, customer = valid ? customer : null, addons } });
        });

        // Catalog icon (assets/icon.png of the version the catalog shows: ?v=, else the newest
        // live, else beta, version); public like the catalog.
        api.MapGet("/packages/{id}/icon", async (string id, string? v, HttpContext ctx, AppDbContext db, SubmissionService svc,
                                                 UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers)) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var cmp = new SemVerComparer();
            var versions = await db.PackageVersions
                .Where(x => x.PackageId == id && (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta)).ToListAsync();
            var pick = (v is null ? null : versions.FirstOrDefault(x => x.Version == v))
                       ?? versions.Where(x => x.Status == VersionStatus.Live).OrderByDescending(x => x.Version, cmp).FirstOrDefault()
                       ?? versions.OrderByDescending(x => x.Version, cmp).FirstOrDefault();
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

        // Screenshots (S0.11.0): list with localized captions (JSON, or TSV "url<TAB>caption" for the client) and images.
        api.MapGet("/packages/{id}/screenshots", async (string id, string? lang, string? format, HttpContext ctx, AppDbContext db,
                                                        UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers)) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var v = await ScreenshotService.DisplayVersionAsync(db, id);
            if (v is null) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var culture = (lang ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).Trim();
            if (culture.Length > 2) culture = MapHostLang(culture);
            var shots = ScreenshotService.FromManifest(v.ManifestJson, culture);
            string Url(int n) => $"{Base(ctx)}/api/packages/{id}/screenshots/{n}?v={v.Version}";
            if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
                return Results.Text(string.Concat(shots.Select(s => Url(s.Index) + "\t" + s.Caption.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\n")),
                                    "text/tab-separated-values; charset=utf-8");
            return Results.Json(new { ok = true, data = new { id, version = v.Version, screenshots = shots.Select(s => new { index = s.Index, url = Url(s.Index), caption = s.Caption }) } });
        });

        api.MapGet("/packages/{id}/screenshots/{n:int}", async (string id, int n, HttpContext ctx, AppDbContext db, SubmissionService svc,
                                                                UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers)) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var v = await ScreenshotService.DisplayVersionAsync(db, id);
            if (v is null) return NotFound("PACKAGE_NOT_FOUND", $"No released package with id '{id}'.");
            var shot = ScreenshotService.FromManifest(v.ManifestJson, "en").FirstOrDefault(s => s.Index == n);
            if (shot is null) return NotFound("SCREENSHOT_NOT_FOUND", $"No screenshot {n}.");
            var img = await ScreenshotService.ReadAsync(Path.Combine(svc.StorageRoot, v.FilePath), shot.File);
            if (img is null) return NotFound("SCREENSHOT_NOT_FOUND", "The screenshot could not be read.");
            return Results.File(img.Value.Bytes, img.Value.Type, lastModified: v.SubmittedAt,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{v.Sha256[..16]}-{n}\""));
        });

        // Ratings and problem reports from Add-on Store clients (anonymous install id, see FeedbackService).
        api.MapPost("/packages/{id}/rating", async (string id, HttpContext ctx, FeedbackService fb, RatingBody body,
                                                    AppDbContext db, UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers))
                return Results.Json(new { ok = false, error = new { code = "PACKAGE_NOT_FOUND", message = $"No released package with id '{id}'.", hint = "" } }, statusCode: 404);
            var (r, sum) = await fb.RateAsync(ctx, id, body.InstallId, body.Stars, body.Version);
            if (!r.Ok) return Results.Json(new { ok = false, error = new { code = r.Code, message = r.Message, hint = "" } },
                                           statusCode: r.Code == "PACKAGE_NOT_FOUND" ? 404 : r.Code == "RATE_LIMITED" ? 429 : 400);
            return Results.Json(new { ok = true, data = new { id, average = sum?.Average ?? 0, count = sum?.Count ?? 0 } });
        });

        api.MapPost("/packages/{id}/feedback", async (string id, HttpContext ctx, FeedbackService fb, FeedbackBody body,
                                                      AppDbContext db, UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers))
                return Results.Json(new { ok = false, error = new { code = "PACKAGE_NOT_FOUND", message = $"No released package with id '{id}'.", hint = "" } }, statusCode: 404);
            var r = await fb.AddAsync(ctx, id, body.InstallId, body.Kind, body.Message, body.Email, body.Version, body.Log);
            if (!r.Ok) return Results.Json(new { ok = false, error = new { code = r.Code, message = r.Message, hint = "" } },
                                           statusCode: r.Code == "PACKAGE_NOT_FOUND" ? 404 : r.Code == "RATE_LIMITED" ? 429 : 400);
            return Results.Json(new { ok = true, data = new { id, message = r.Message } });
        });

        // Owner/admin: read and close reports (portal and Claude sessions).
        api.MapGet("/packages/{id}/feedback", async (string id, string? status, HttpContext ctx, AppDbContext db, UserManager<AppUser> users) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var pkg = await db.Packages.FirstOrDefaultAsync(p => p.Id == id);
            if (pkg is null) return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");
            if (pkg.OwnerId != user.Id && !ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "NOT_OWNER", message = "This package belongs to another user.", hint = "Only the owner or an admin can read its feedback." } }, statusCode: 403);
            var q = db.Feedbacks.AsNoTracking().Where(f => f.PackageId == id);
            if (status is "open" or "done") q = q.Where(f => f.Status == status);
            var rows = await q.OrderByDescending(f => f.Id).Take(500).ToListAsync();
            var ratings = await db.Ratings.AsNoTracking().Where(r => r.PackageId == id).GroupBy(r => r.Stars)
                .Select(g => new { stars = g.Key, count = g.Count() }).ToListAsync();
            return Results.Json(new
            {
                ok = true,
                data = new
                {
                    id,
                    ratings = Enumerable.Range(1, 5).ToDictionary(s => s.ToString(), s => ratings.FirstOrDefault(r => r.stars == s)?.count ?? 0),
                    feedback = rows.Select(f => new
                    {
                        f.Id, f.Kind, f.Version, f.Message, f.Email, f.ClientInfo, log = f.LogExcerpt, f.Country, f.CreatedAt, f.Status, f.DoneAt, f.DoneBy,
                        ai = f.AiAt is null ? null : new
                        {
                            category = f.AiCategory, severity = f.AiSeverity, language = f.AiLanguage, summaryEn = f.AiSummaryEn,
                            summaryDe = f.AiSummaryDe, suggestedReply = f.AiReply, duplicateOf = f.AiDuplicateOf, at = f.AiAt
                        }
                    })
                }
            });
        }).RequireAuthorization("ApiOrCookie");

        api.MapMethods("/packages/{id}/feedback/{fid:int}", new[] { "PATCH" }, async (string id, int fid, HttpContext ctx, AppDbContext db,
            UserManager<AppUser> users, AuditService audit, FeedbackStatusBody body) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var pkg = await db.Packages.FirstOrDefaultAsync(p => p.Id == id);
            if (pkg is null) return NotFound("PACKAGE_NOT_FOUND", $"No package with id '{id}'.");
            if (pkg.OwnerId != user.Id && !ctx.User.IsInRole("Admin"))
                return Results.Json(new { ok = false, error = new { code = "NOT_OWNER", message = "This package belongs to another user.", hint = "" } }, statusCode: 403);
            var f = await db.Feedbacks.FirstOrDefaultAsync(x => x.Id == fid && x.PackageId == id);
            if (f is null) return NotFound("FEEDBACK_NOT_FOUND", $"No feedback {fid} for '{id}'.");
            if (body.Status is not ("open" or "done"))
                return Results.Json(new { ok = false, error = new { code = "FEEDBACK_STATUS_INVALID", message = "status must be 'open' or 'done'.", hint = "" } }, statusCode: 400);
            f.Status = body.Status;
            f.DoneAt = body.Status == "done" ? DateTime.UtcNow : null;
            f.DoneBy = body.Status == "done" ? user.DisplayName : null;
            await db.SaveChangesAsync();
            await audit.LogAsync(user.DisplayName, "feedback." + body.Status, $"{id} #{fid}");
            return Results.Json(new { ok = true, data = new { id, feedback = fid, status = f.Status } });
        }).RequireAuthorization("ApiOrCookie");

        // Optional AI features (S0.12.0). The probe tells clients which ones are switched on.
        api.MapGet("/features", async (AiService ai) =>
        {
            var cfg = await ai.ConfigAsync();
            return Results.Json(new { ok = true, data = new { aiSearch = cfg.On && cfg.Search, aiTriage = cfg.On && cfg.Triage, aiReview = cfg.On && cfg.Review } });
        });

        // Search by need ("I want to split invoices by barcode"): AI ranking with reasons when enabled, else word search.
        api.MapGet("/search", async (string? q, string? lang, string? channel, string? format, HttpContext ctx, AiAssist assist,
                                     IMemoryCache cache) =>
        {
            q = (q ?? "").Trim();
            if (q.Length < 2 || q.Length > 300)
                return Results.Json(new { ok = false, error = new { code = "QUERY_INVALID", message = "q must have 2 to 300 characters.", hint = "" } }, statusCode: 400);
            var culture = (lang ?? "en").Trim();
            if (culture.Length > 2) culture = MapHostLang(culture);
            var beta = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase);
            // At most 40 AI searches per address and day; after that the plain word search answers.
            var ip = GeoService.ClientIp(ctx)?.ToString() ?? "";
            var counter = cache.GetOrCreate("ai-search-ip:" + ip + ":" + DateTime.UtcNow.ToString("yyyyMMdd"),
                e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1); return new int[1]; })!;
            bool allowAi;
            lock (counter) { allowAi = counter[0] < 40; if (allowAi) counter[0]++; }
            var (usedAi, hits) = await assist.SearchAsync(q, culture, beta, allowAi, ctx.RequestAborted);
            if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
                return Results.Text((usedAi ? "#ai\n" : "#text\n") + string.Concat(hits.Select(h =>
                    h.Id + "\t" + h.Reason.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\n")),
                    "text/tab-separated-values; charset=utf-8");
            return Results.Json(new { ok = true, data = new { query = q, ai = usedAi, results = hits.Select(h => new { id = h.Id, name = h.Name, reason = h.Reason }) } });
        });

        // Review aid for one version (reviewers/admins): GET the stored one, POST to (re)create it.
        api.MapGet("/packages/{id}/{version}/ai-review", async (string id, string version, AppDbContext db) =>
        {
            var v = await db.PackageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No version {version} of '{id}'.");
            if (v.AiReviewJson is null) return NotFound("AI_REVIEW_MISSING", "No review aid has been created for this version.");
            using var doc = System.Text.Json.JsonDocument.Parse(v.AiReviewJson);
            return Results.Json(new { ok = true, data = new { id, version, model = v.AiReviewModel, at = v.AiReviewAt, language = v.AiReviewLang, review = doc.RootElement.Clone() } });
        }).RequireAuthorization("ReviewerOrAdmin");

        api.MapPost("/packages/{id}/{version}/ai-review", async (string id, string version, string? lang, HttpContext ctx, AppDbContext db,
                                                               AiService ai, AiAssist assist, AuditService audit, UserManager<AppUser> users) =>
        {
            var cfg = await ai.ConfigAsync();
            if (!cfg.On || !cfg.Review)
                return Results.Json(new { ok = false, error = new { code = "AI_OFF", message = "The AI review aid is switched off.", hint = "An admin can switch it on in Admin > Settings." } }, statusCode: 409);
            var v = await db.PackageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version);
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No version {version} of '{id}'.");
            if (!await assist.ReviewAsync(v, AiAssist.ReviewLanguage(lang), ctx.RequestAborted))
                return Results.Json(new { ok = false, error = new { code = "AI_FAILED", message = "The AI provider gave no usable answer.", hint = "Check the connection test in Admin > Settings and the server log." } }, statusCode: 502);
            var user = await users.GetUserAsync(ctx.User);
            await audit.LogAsync(user?.DisplayName ?? "?", "ai.review", $"{id} {version}");
            var stored = await db.PackageVersions.AsNoTracking().FirstAsync(x => x.Id == v.Id);
            using var doc = System.Text.Json.JsonDocument.Parse(stored.AiReviewJson!);
            return Results.Json(new { ok = true, data = new { id, version, model = stored.AiReviewModel, at = stored.AiReviewAt, language = stored.AiReviewLang, review = doc.RootElement.Clone() } });
        }).RequireAuthorization("ReviewerOrAdmin");

        // ---- Customer deliveries (S0.14.0): customers, codes, deliveries ----
        static IResult Fail(string code, string message, int status, string hint = "") =>
            Results.Json(new { ok = false, error = new { code, message, hint } }, statusCode: status);
        static string UiLang(HttpContext c) => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        // A date without an offset counts as UTC (create and change alike).
        static DateTime? AsUtc(DateTime? d) => d is null ? null
            : d.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) : d.Value.ToUniversalTime();

        api.MapGet("/customers", async (HttpContext ctx, AppDbContext db, UserManager<AppUser> users, CustomerService cs) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var list = await cs.Visible(ctx.User, user.Id).OrderBy(c => c.Name).ToListAsync();
            var counts = await db.Deliveries.GroupBy(d => d.CustomerId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
            return Results.Json(new { ok = true, data = list.Select(c => new
            {
                id = c.Id, name = c.Name, status = c.Status, contactName = c.ContactName, language = c.Language,
                deliveries = counts.GetValueOrDefault(c.Id), lastSeenAt = c.LastSeenAt, createdAt = c.CreatedAt,
                mine = c.OwnerId == user.Id,
            }) });
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/customers", async (HttpContext ctx, UserManager<AppUser> users, CustomerService cs, CustomerBody body) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            if (ctx.User.IsInRole("Reviewer") && !ctx.User.IsInRole("Admin"))
                return Fail("NOT_OWNER", "Reviewers read customers; creating them is for developers and admins.", 403);
            if (CustomerService.CheckCustomer(body.Name, body.ContactEmail, body.Language) is { } err)
                return Fail(err, "name (1 to 120 characters) is required; contactEmail must be an address; language a two-letter code.", 400,
                    "Example: {\"name\": \"Muster AG\", \"contactName\": \"Erika Muster\", \"contactEmail\": \"it@muster.example\", \"language\": \"de\"}");
            var c = await cs.CreateCustomerAsync(body.Name!, body.ContactName, body.ContactEmail, body.Language, body.Note, user, body.WithCode ?? true);
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c, true, UiLang(ctx)) }, statusCode: 201);
        }).RequireAuthorization("ApiOrCookie");

        api.MapGet("/customers/{cid:int}", async (int cid, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, CustomerService cs) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c, CustomerService.CanManage(ctx.User, c, user.Id), UiLang(ctx)) });
        }).RequireAuthorization("ApiOrCookie");

        api.MapMethods("/customers/{cid:int}", new[] { "PATCH" }, async (int cid, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
                                                                         CustomerService cs, CustomerBody body) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            if (!CustomerService.CanManage(ctx.User, c, user.Id)) return Fail("NOT_OWNER", "Only the creator of the customer or an admin can change it.", 403);
            if (CustomerService.CheckCustomer(body.Name ?? c.Name, body.ContactEmail, body.Language) is { } err ||
                body.Status is not (null or "active" or "paused"))
                return Fail("CUSTOMER_INVALID", "Check name, contactEmail, language and status (active or paused).", 400);
            await cs.UpdateCustomerAsync(c, body.Name, body.ContactName ?? c.ContactName, body.ContactEmail ?? c.ContactEmail,
                body.Language, body.Note ?? c.Note, body.Status, user.DisplayName);
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c, true, UiLang(ctx)) });
        }).RequireAuthorization("ApiOrCookie");

        api.MapDelete("/customers/{cid:int}", async (int cid, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, CustomerService cs) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            if (!CustomerService.CanManage(ctx.User, c, user.Id)) return Fail("NOT_OWNER", "Only the creator of the customer or an admin can delete it.", 403);
            await cs.DeleteCustomerAsync(c, user.DisplayName);
            return Results.Json(new { ok = true, data = new { deleted = cid } });
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/customers/{cid:int}/codes", async (int cid, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
                                                          CustomerService cs, CodeBody? body) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            if (!CustomerService.CanManage(ctx.User, c, user.Id)) return Fail("NOT_OWNER", "Only the creator of the customer or an admin can create codes.", 403);
            if (body?.DeliveryId is int did && !await db.Deliveries.AnyAsync(d => d.Id == did && d.CustomerId == cid))
                return NotFound("DELIVERY_NOT_FOUND", $"No delivery {did} for customer {cid}.");
            var (code, plain) = await cs.CreateCodeAsync(cid, body?.DeliveryId, user.DisplayName, body?.TransitionDays ?? 14);
            return Results.Json(new { ok = true, data = new { id = code.Id, scope = code.DeliveryId is null ? "customer" : "delivery",
                deliveryId = code.DeliveryId, code = plain } }, statusCode: 201);
        }).RequireAuthorization("ApiOrCookie");

        api.MapDelete("/customers/{cid:int}/codes/{codeId:int}", async (int cid, int codeId, HttpContext ctx, AppDbContext db,
                                                                         UserManager<AppUser> users, CustomerService cs) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            if (!CustomerService.CanManage(ctx.User, c, user.Id)) return Fail("NOT_OWNER", "Only the creator of the customer or an admin can revoke codes.", 403);
            return await cs.RevokeCodeAsync(cid, codeId, user.DisplayName)
                ? Results.Json(new { ok = true, data = new { id = codeId, revoked = true } })
                : NotFound("CODE_NOT_FOUND", $"No code {codeId} for customer {cid}.");
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/customers/{cid:int}/deliveries", async (int cid, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
                                                               CustomerService cs, DeliveryBody body) =>
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return Unauthorized();
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == cid);
            if (c is null || !CustomerService.CanSee(ctx.User, c, user.Id)) return NotFound("CUSTOMER_NOT_FOUND", $"No customer {cid}.");
            if (!CustomerService.CanManage(ctx.User, c, user.Id)) return Fail("NOT_OWNER", "Only the creator of the customer or an admin can deliver to it.", 403);
            var r = await cs.CreateDeliveryAsync(c, body.PackageId ?? "", new(body.Beta?.Mode, body.Beta?.Version),
                new(body.Live?.Mode, body.Live?.Version), AsUtc(body.StartsAt), AsUtc(body.EndsAt), body.OwnCode ?? false, user, ctx.User.IsInRole("Admin"));
            if (!r.Ok) return Fail(r.Code, r.Message, r.Code is "PACKAGE_NOT_FOUND" ? 404 : r.Code is "NOT_OWNER" ? 403 : r.Code is "DELIVERY_EXISTS" ? 409 : 400,
                "beta/live: {\"mode\": \"latest\"|\"fixed\"|\"off\", \"version\": \"1.2.0\"}; live defaults to the newest version, fixed.");
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c, true, UiLang(ctx)) }, statusCode: 201);
        }).RequireAuthorization("ApiOrCookie");

        async Task<(Delivery? D, Customer? C, IResult? Error, AppUser? User)> LoadDelivery(int did, HttpContext ctx, AppDbContext db,
                                                                                           UserManager<AppUser> users)
        {
            var user = await RequireUserAsync(ctx, users);
            if (user is null) return (null, null, Unauthorized(), null);
            var d = await db.Deliveries.FirstOrDefaultAsync(x => x.Id == did);
            var c = d is null ? null : await db.Customers.FirstOrDefaultAsync(x => x.Id == d.CustomerId);
            if (d is null || c is null || !CustomerService.CanSee(ctx.User, c, user.Id))
                return (null, null, NotFound("DELIVERY_NOT_FOUND", $"No delivery {did}."), user);
            if (!CustomerService.CanManage(ctx.User, c, user.Id) ||
                (!ctx.User.IsInRole("Admin") && !await db.Packages.AnyAsync(p => p.Id == d.PackageId && p.OwnerId == user.Id)))
                return (null, null, Fail("NOT_OWNER", "Only the add-on's owner (as creator of the customer) or an admin can change this delivery.", 403), user);
            return (d, c, null, user);
        }

        api.MapMethods("/deliveries/{did:int}", new[] { "PATCH" }, async (int did, HttpContext ctx, AppDbContext db, UserManager<AppUser> users,
                                                                           CustomerService cs, DeliveryBody body) =>
        {
            var (d, c, error, user) = await LoadDelivery(did, ctx, db, users);
            if (error is not null) return error;
            var clear = body.ClearDates == true;
            var r = await cs.UpdateDeliveryAsync(d!, body.Beta is null ? null : new(body.Beta.Mode, body.Beta.Version),
                body.Live is null ? null : new(body.Live.Mode, body.Live.Version),
                clear ? null : (AsUtc(body.StartsAt) ?? d!.StartsAt), clear ? null : (AsUtc(body.EndsAt) ?? d!.EndsAt),
                body.StartsAt is not null || body.EndsAt is not null || clear, body.Status, user!, ctx.User.IsInRole("Admin"));
            if (!r.Ok) return Fail(r.Code, r.Message, 400);
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c!, true, UiLang(ctx)) });
        }).RequireAuthorization("ApiOrCookie");

        api.MapPost("/deliveries/{did:int}/promote", async (int did, HttpContext ctx, AppDbContext db, UserManager<AppUser> users, CustomerService cs) =>
        {
            var (d, c, error, user) = await LoadDelivery(did, ctx, db, users);
            if (error is not null) return error;
            var r = await cs.PromoteAsync(d!, user!, ctx.User.IsInRole("Admin"));
            if (!r.Ok) return Fail(r.Code, r.Message, 409);
            return Results.Json(new { ok = true, data = await cs.DescribeAsync(c!, true, UiLang(ctx)) });
        }).RequireAuthorization("ApiOrCookie");

        api.MapGet("/categories", async (AppDbContext db, CategoryService categories) =>
        {
            var all = await categories.AllAsync();
            var pkgs = await db.Packages.Include(p => p.Versions).Where(p => p.Visibility != "private").ToListAsync();
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
                ? el.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Last().Value.GetString() ?? "")
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
                    case "visibility":
                        change.SetVisibility = true; change.Visibility = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        break;
                    default:
                        typeErrors.Add(new("METADATA_INVALID", "error", $"Unknown field '{prop.Name}'.", "Allowed fields: name, description, author, contactEmail, category, visibility."));
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
            UserManager<AppUser> users, AppDbContext db, VersionActionService actions) =>
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
            if (v.Status is not (VersionStatus.Beta or VersionStatus.Live))
                return Results.Json(new { ok = false, error = new { code = "VERSION_NOT_WITHDRAWABLE", message = $"Version {version} is {v.Status.ToString().ToLowerInvariant()} and cannot be withdrawn.", hint = "Only beta versions (and, for admins, live versions) can be withdrawn." } }, statusCode: 409);

            var pinned = await db.Deliveries.CountAsync(d => d.PackageId == id && (d.LiveVersion == version || d.BetaVersion == version) && d.Status == "active");
            await actions.WithdrawAsync(v.Id, user, isAdmin);
            return Results.Json(new { ok = true, data = new { id, version, status = "withdrawn",
                warning = pinned > 0 ? $"{pinned} customer deliveries were fixed to this version and hand out nothing now; change them." : null } });
        }).RequireAuthorization("BearerOnly");

        api.MapGet("/packages/{id}/{version}/download", async (string id, string version,
            AppDbContext db, SubmissionService svc, UsageService usage, HttpContext ctx, UserManager<AppUser> users, CustomerService customers) =>
        {
            if (!await MayAccessAsync(ctx, id, db, users, customers, version))
                return NotFound("VERSION_NOT_FOUND", $"No downloadable version {version} of '{id}'.");
            var v = await db.PackageVersions.FirstOrDefaultAsync(x => x.PackageId == id && x.Version == version &&
                (x.Status == VersionStatus.Live || x.Status == VersionStatus.Beta));
            if (v is null) return NotFound("VERSION_NOT_FOUND", $"No downloadable version {version} of '{id}'.");
            var path = Path.Combine(svc.StorageRoot, v.FilePath);
            if (!File.Exists(path)) return NotFound("FILE_MISSING", "The package file is missing on the server; contact an admin.");
            // atomic increment: concurrent downloads must not lose counts
            await db.PackageVersions.Where(x => x.Id == v.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Downloads, x => x.Downloads + 1));
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

    /// <summary>
    /// Markdown documents for assistants: text/markdown only when the caller asks for it
    /// (Accept), otherwise text/plain. The web readers of several assistants refuse
    /// text/markdown outright although the text is the same.
    /// </summary>
    private static IResult MarkdownText(HttpContext ctx, string text)
    {
        var accept = ctx.Request.Headers.Accept.ToString();
        var type = accept.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) ? "text/markdown" : "text/plain";
        // LF everywhere: the texts are raw string literals, whose line ends follow the checkout (CRLF on Windows).
        return Results.Text(text.Replace("\r\n", "\n"), type + "; charset=utf-8");
    }

    /// <summary>Maps Power PDF's 3-letter resource codes (DEU, FRA, ...) to two-letter culture names.</summary>
    private static string MapHostLang(string code) => code.Length > 2 && (code[2] == '-' || code[2] == '_')
        ? code[..2].ToLowerInvariant() is var two && PackageValidator.RequiredLanguages.Contains(two) ? two : "en"
        : code.ToUpperInvariant() switch
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

    private static string GuideHtml(string title, string md) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
        $"<title>{System.Net.WebUtility.HtmlEncode(title)}</title></head>" +
        "<body style=\"font-family:Arial,sans-serif;max-width:980px;margin:24px auto;padding:0 16px;color:#002854\">" +
        "<p>Full guide: <a href=\"/agent-guide\">/agent-guide</a> (Markdown: <a href=\"/api/agent-guide\">/api/agent-guide</a>).</p>" +
        "<pre style=\"white-space:pre-wrap;word-wrap:break-word;font:14px/1.5 Consolas,monospace\">" +
        System.Net.WebUtility.HtmlEncode(md) + "</pre></body></html>";

    private static IResult BundleInvalid(string message) => Results.Json(new
    {
        ok = false,
        error = new
        {
            code = "BUNDLE_INVALID",
            message,
            hint = "An upload package is a ZIP with exactly one .ppak and at most one source ZIP of the same version (make-ppak.ps1 -Source builds it); or upload the .ppak alone."
        }
    }, statusCode: 400);

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

    private static bool TooManyUploads(HttpContext ctx, IMemoryCache cache)
    {
        var who = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? GeoService.ClientIp(ctx)?.ToString() ?? "?";
        var counter = cache.GetOrCreate("uploads:" + who + ":" + DateTime.UtcNow.ToString("yyyyMMddHH"),
            e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); return new int[1]; })!;
        lock (counter) { return ++counter[0] > 60; }
    }

    private static async Task<string?> SaveUploadAsync(HttpRequest request)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ppak-" + Guid.NewGuid().ToString("N") + ".zip");
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files["package"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return null;
            try
            {
                await using var fs = File.Create(tmp);
                await file.CopyToAsync(fs);
            }
            catch { TryDelete(tmp); throw; }
            return tmp;
        }
        if (request.ContentLength is null or 0) return null;
        try
        {
            await using (var fs = File.Create(tmp))
                await request.Body.CopyToAsync(fs);
        }
        catch
        {
            TryDelete(tmp);   // aborted or oversized upload: no 220 MB leftovers in TEMP
            throw;
        }
        if (new FileInfo(tmp).Length > 0) return tmp;
        TryDelete(tmp);
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp cleanup is best effort */ }
    }

    public static async Task<List<object>> CatalogAsync(AppDbContext db, bool includeBeta, string baseUrl,
                                                         List<CustomerService.Granted>? grants = null, PackageSigning? signing = null)
    {
        if (signing is not null) await signing.EnsureLoadedAsync();
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
        var ratingSums = await FeedbackService.SummariesAsync(db);
        // Share page slugs over live and beta ids, the same set /a/{slug} resolves against.
        var slugIds = (await db.PackageVersions.Where(v => v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta)
            .Where(v => db.Packages.Any(p => p.Id == v.PackageId && p.Visibility != "private"))
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
            if (pkgs.GetValueOrDefault(pick.PackageId)?.Visibility == "private") continue;
            if (grants?.Any(g => g.Package.Id == pick.PackageId && CustomerService.Pick(g, includeBeta).Version is not null) == true) continue;
            result.Add(Entry(pick, channel, null));
        }
        var delivered = new HashSet<string>();
        foreach (var g in grants ?? new())
        {
            var (v, ch) = CustomerService.Pick(g, includeBeta);
            if (v is not null && delivered.Add(v.PackageId)) result.Add(Entry(v, ch, g.Customer.Name));
        }
        return result;

        object Entry(PackageVersion pick, string channel, string? customer)
        {
            using var doc = JsonDocument.Parse(pick.ManifestJson);
            var root = doc.RootElement;
            var pkg = pkgs.GetValueOrDefault(pick.PackageId);
            var isPrivate = pkg?.Visibility == "private";
            return new
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
                rating = ratingSums.GetValueOrDefault(pick.PackageId) is { } rs ? new { average = rs.Average, count = rs.Count } : null,
                screenshots = ScreenshotService.Count(pick.ManifestJson),
                pageUrl = pick.PackageId == SubmissionService.ClientPackageId || isPrivate ? null
                    : $"{baseUrl}/a/{ShareService.Slug(pick.PackageId, slugIds)}",
                downloadUrl = $"{baseUrl}/api/packages/{pick.PackageId}/{pick.Version}/download",
                iconUrl = $"{baseUrl}/api/packages/{pick.PackageId}/icon?v={Uri.EscapeDataString(pick.Version)}",
                zxtName = ZxtNameOf(root),
                customer,
                signature = signing?.Sign(pick.PackageId, pick.Version, pick.Sha256, ZxtNameOf(root)),
            };
        }
    }

    /// <summary>
    /// Private packages (S0.14.0) are visible to their owner, admins and
    /// reviewers, and to clients whose customer code unlocks a delivery of
    /// them (a given version only when a delivery stage hands it out).
    /// Everyone else gets 404, so the package's existence is not revealed.
    /// </summary>
    private static async Task<bool> MayAccessAsync(HttpContext ctx, string packageId, AppDbContext db, UserManager<AppUser> users,
                                                   CustomerService customers, string? version = null)
    {
        var pkg = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == packageId);
        if (pkg is null || pkg.Visibility != "private") return true;
        var u = await TryUserAsync(ctx, users);
        if (u is not null && (pkg.OwnerId == u.Id || ctx.User.IsInRole("Admin") || ctx.User.IsInRole("Reviewer"))) return true;
        return version is null ? await customers.MaySeeAsync(ctx, pkg) : await customers.MayDownloadAsync(ctx, pkg, version);
    }

    /// <summary>Base name of the x64 binary ("SmartBookmarks" for x64/SmartBookmarks.zxt).</summary>
    public static string ZxtNameOf(JsonElement manifest) =>
        manifest.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Object &&
        f.TryGetProperty("x64", out var x) && x.ValueKind == JsonValueKind.String
            ? Path.GetFileNameWithoutExtension(x.GetString() ?? "") : "";

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

/// <summary>POST /api/packages/{id}/rating</summary>
public record RatingBody(string? InstallId, int Stars, string? Version);
/// <summary>POST /api/packages/{id}/feedback</summary>
public record FeedbackBody(string? InstallId, string? Kind, string? Message, string? Email, string? Version, string? Log);
/// <summary>PATCH /api/packages/{id}/feedback/{fid}</summary>
public record FeedbackStatusBody(string? Status);

/// <summary>POST/PATCH /api/customers[/{cid}] (S0.14.0)</summary>
public record CustomerBody(string? Name, string? ContactName, string? ContactEmail, string? Language, string? Note, string? Status, bool? WithCode);
/// <summary>POST /api/customers/{cid}/codes: deliveryId null = code for all deliveries of the customer</summary>
public record CodeBody(int? DeliveryId, int? TransitionDays);
public record StageBody(string? Mode, string? Version);
/// <summary>POST /api/customers/{cid}/deliveries and PATCH /api/deliveries/{did}</summary>
public record DeliveryBody(string? PackageId, StageBody? Beta, StageBody? Live, DateTime? StartsAt, DateTime? EndsAt,
                           bool? ClearDates, string? Status, bool? OwnCode);

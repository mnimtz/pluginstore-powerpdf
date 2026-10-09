using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// The add-on choice of deliveries and delivery templates (S1.14.0): one searchable, paged list with
/// several add-ons at a time instead of a drop-down with every add-on (Pages/Shared/_AddonPicker.cshtml).
/// </summary>
public static class AddonPicker
{
    /// <summary>One add-on to choose. Versions: approved ones, newest first; Waiting: none approved yet
    /// (a delivery follows "newest" until the first approval); Present: delivered or in the template already.</summary>
    public record Item(string Id, string Name, string Visibility, bool Mine, string Author, string Category,
                       string CategoryName, List<string> Versions, bool Waiting, bool Present);

    /// <summary>Key: id prefix of the page elements; FormId: the form the selection goes into; SubmitOnApply: the
    /// dialog's button sends the form (templates), otherwise it fills the selection above the form's own button.</summary>
    /// <summary>PresentSelectable: an add-on that is there already can be chosen again (templates: it gets the new version).</summary>
    public record Model(string Key, string FormId, bool SubmitOnApply, string PresentLabel, List<Item> Items, bool PresentSelectable = false);

    /// <summary>Every add-on but the store client, with three queries for all of them (the production database
    /// sits on a network share: one query per add-on made the page slow with many add-ons).</summary>
    public static async Task<List<Item>> BuildAsync(AppDbContext db, string culture, string? userId,
                                                    ISet<string> present, bool withWaiting)
    {
        var pkgs = await db.Packages.AsNoTracking().Include(p => p.Owner)
            .Where(p => p.Id != SubmissionService.ClientPackageId).ToListAsync();
        var versions = (await db.PackageVersions.AsNoTracking()
                .Where(v => v.PackageId != SubmissionService.ClientPackageId &&
                            (v.Status == VersionStatus.Live || v.Status == VersionStatus.Beta || v.Status == VersionStatus.Submitted))
                .ToListAsync())
            .GroupBy(v => v.PackageId).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version, new SemVerComparer()).ToList());
        var known = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Slug);
        var items = new List<Item>();
        foreach (var p in pkgs)
        {
            var all = versions.GetValueOrDefault(p.Id) ?? new List<PackageVersion>();
            var approved = all.Where(v => v.Status is VersionStatus.Live or VersionStatus.Beta).ToList();
            var shown = approved.FirstOrDefault() ?? all.FirstOrDefault();
            if (shown is null || (approved.Count == 0 && !withWaiting)) continue;   // nothing that passed the checks yet
            using var doc = JsonDocument.Parse(shown.ManifestJson);
            var cat = CatalogUi.EffectiveCategory(p, doc.RootElement, known);
            items.Add(new Item(p.Id, CatalogUi.DisplayName(p, shown, culture), p.Visibility, userId is not null && p.OwnerId == userId,
                CatalogUi.EffectiveAuthor(p, doc.RootElement, CatalogUi.PublicName(p.Owner)), cat,
                CategoryService.Name(known.GetValueOrDefault(cat), cat, culture),
                approved.Select(v => v.Version).ToList(), approved.Count == 0, present.Contains(p.Id)));
        }
        // private ones first (they are what deliveries are mostly for), then by name
        return items.OrderBy(i => i.Visibility == "private" ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The add-ons of a picker form: packageIds plus "ver:{id}" per add-on (empty = newest approved).</summary>
    public static List<(string Id, string? Version)> Selection(IFormCollection form)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string?)>();
        foreach (var raw in form["packageIds"])
        {
            var id = (raw ?? "").Trim();
            if (id.Length == 0 || id.Length > 200 || !seen.Add(id) || list.Count >= 200) continue;
            var v = form["ver:" + id].ToString().Trim();
            list.Add((id, v.Length == 0 || v.Length > 40 ? null : v));
        }
        return list;
    }
}

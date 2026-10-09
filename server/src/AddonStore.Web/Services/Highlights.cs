using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Start page highlights (S1.16.0): admins mark public add-ons; the catalog shows them first, so on the first
/// page, with a glowing frame and a "Highlight" badge. At most one page of them (9 tiles).
/// </summary>
public static class Highlights
{
    public const int Max = 9;

    /// <summary>Why the change is not possible, or null.</summary>
    /// <param name="visibility">the visibility the add-on ends up with (a PATCH may change both at once)</param>
    public static async Task<string?> CheckAsync(AppDbContext db, Package pkg, bool on, string? visibility = null)
    {
        if (!on) return null;
        if ((visibility ?? pkg.Visibility) == "private" || pkg.Id == SubmissionService.ClientPackageId) return "Only public add-ons can be highlights.";
        if (pkg.FeaturedAt is not null) return null;
        // S1.17.3: count the highlights the start page shows; a blocked or withdrawn one has no tile (and no star to remove it)
        var shown = (await CatalogUi.GetAsync(db, "en", includeBeta: false)).Count(i => i.Featured && i.Id != pkg.Id);
        return shown >= Max ? "At most 9 add-ons can be highlights." : null;
    }

    /// <summary>Sets or removes the highlight (after <see cref="CheckAsync"/>); unchanged state does nothing.</summary>
    public static async Task SetAsync(AppDbContext db, AuditService audit, Package pkg, bool on, AppUser actor)
    {
        if (on == (pkg.FeaturedAt is not null)) return;
        pkg.FeaturedAt = on ? DateTime.UtcNow : null;
        pkg.FeaturedBy = on ? actor.DisplayName : null;
        await db.SaveChangesAsync();
        await audit.LogAsync(actor.DisplayName, on ? "package.highlighted" : "package.highlight.removed", pkg.Id);
    }
}

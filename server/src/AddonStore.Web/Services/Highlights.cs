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
    public static async Task<string?> CheckAsync(AppDbContext db, Package pkg, bool on)
    {
        if (!on || pkg.FeaturedAt is not null) return null;
        if (pkg.Visibility == "private" || pkg.Id == SubmissionService.ClientPackageId) return "Only public add-ons can be highlights.";
        if (await db.Packages.CountAsync(p => p.FeaturedAt != null && p.Visibility != "private") >= Max)
            return "At most 9 add-ons can be highlights.";
        return null;
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

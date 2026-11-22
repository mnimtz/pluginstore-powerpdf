using System.Text.RegularExpressions;
using AddonStore.Web.Data;
using AddonStore.Web.Validation;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.5.0: the minimum Power PDF version of an add-on. Each version brings its own value in the manifest
/// (minPowerPdfVersion); an admin can set a value for the whole add-on in "All plug-ins", which then wins for
/// every version, also later ones, until it is reset. The catalog (store window), the add-on page and the
/// manual ZIP use the effective value; the version dossier keeps showing what the package declared.
/// </summary>
public static class MinHost
{
    // the same shape the upload check accepts for minPowerPdfVersion (PackageValidator.HostVersionPattern)
    private static readonly Regex Pattern = new(@"^[0-9]{1,4}(\.[0-9]{1,6}){0,3}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>A version such as 2025.3 or 2025.3.7.0.</summary>
    public static bool IsValid(string? value) => !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);

    /// <summary>
    /// The value in force for <paramref name="v"/>: the higher of the admin's value and the version's own (S2.7.1, review:
    /// an admin value below a later version's own minimum let the store window offer it on a Power PDF that is too old,
    /// while the manual ZIP refused it). Admins raise the minimum, they never lower a developer's.
    /// </summary>
    public static string Effective(Package? pkg, PackageVersion? v)
    {
        var own = (v?.MinPowerPdfVersion ?? "").Trim();
        var admin = (pkg?.MinPowerPdfOverride ?? "").Trim();
        if (admin.Length == 0 || !IsValid(admin)) return own;
        if (own.Length == 0 || !IsValid(own)) return admin;
        return new SemVerComparer().Compare(admin, own) > 0 ? admin : own;
    }

    /// <summary>
    /// Sets or resets (null or empty) the admin's value. A value equal to the package's own (<paramref name="packageValue"/>)
    /// is stored as "no value", so a later version with another minimum is not silently overruled. Returns an error text
    /// (a resource key) or null; writes the audit log when something changed.
    /// </summary>
    public static async Task<string?> SetAsync(AppDbContext db, AuditService audit, Package pkg, string? value, string packageValue, string actor)
    {
        var v = (value ?? "").Trim();
        if (v.Length > 0 && !IsValid(v)) return "Enter a version such as 2025.3 or 2025.3.7.";
        var own = (packageValue ?? "").Trim();
        if (v.Length > 0 && IsValid(own) && new SemVerComparer().Compare(v, own) < 0)
            return "An admin value can only raise the minimum: the package itself requires a higher Power PDF version.";
        string? stored = v.Length == 0 || v == own ? null : v;
        if (stored == pkg.MinPowerPdfOverride) return null;
        var before = pkg.MinPowerPdfOverride;
        pkg.MinPowerPdfOverride = stored;
        await db.SaveChangesAsync();
        CatalogUi.Invalidate();
        await audit.LogAsync(actor, "package.minhost", pkg.Id, $"{before ?? "package value"} -> {stored ?? "package value"}");
        return null;
    }

    /// <summary>Choices for the field: every minimum the store knows (manifests and admin values), newest first.</summary>
    public static async Task<List<string>> SuggestionsAsync(AppDbContext db)
    {
        var fromVersions = (await LightVersions.AllAsync(db)).Select(v => (v.MinPowerPdfVersion ?? "").Trim());
        var fromAdmins = await db.Packages.AsNoTracking().Where(p => p.MinPowerPdfOverride != null).Select(p => p.MinPowerPdfOverride!).ToListAsync();
        return fromVersions.Concat(fromAdmins).Where(IsValid).Distinct()
            .OrderByDescending(x => x, new SemVerComparer()).Take(40).ToList();
    }
}

using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Display names identify people in audit entries, notes and assignments, so no two
/// accounts share one, ignoring case (audit S1.3.1).
/// </summary>
public static class DisplayNames
{
    public static async Task<bool> TakenAsync(IQueryable<AppUser> users, string name, string? exceptUserId = null)
    {
        // compared in .NET, culture-independent (S1.4.3): SQLite lower() folds ASCII only, ToLower() follows the UI culture
        var n = name.Trim();
        var names = await users.Where(u => u.Id != exceptUserId).Select(u => u.DisplayName).ToListAsync();
        return names.Any(x => string.Equals(x.Trim(), n, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(x.Trim().ToUpperInvariant(), n.ToUpperInvariant(), StringComparison.Ordinal));
    }
}

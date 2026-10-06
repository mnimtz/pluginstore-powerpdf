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
        var n = name.Trim().ToLower();
        return await users.AnyAsync(u => u.Id != exceptUserId && u.DisplayName.ToLower() == n);
    }
}

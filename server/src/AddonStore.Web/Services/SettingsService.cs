using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Runtime instance settings, stored in the database and editable on the
/// admin Settings page. Falls back to appsettings/environment configuration
/// so container-level values keep working.
/// Known keys: Email.ResendApiKey, Email.From, App.PublicBaseUrl.
/// </summary>
public class SettingsService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public SettingsService(AppDbContext db, IConfiguration config)
    {
        _db = db; _config = config;
    }

    // S2.0.4: values kept in memory for half a minute (every page asked the database for several settings; on the
    // data share each query is a network round trip); SetAsync updates the entry at once, a restore clears it
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Value, DateTime At)> Cache = new();
    private static readonly TimeSpan CacheAge = TimeSpan.FromSeconds(30);

    /// <summary>Forgets every cached value (after a restore replaced the database).</summary>
    public static void ClearCache() => Cache.Clear();

    public async Task<string> GetAsync(string key, string configKey = "")
    {
        string? value;
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheAge) value = hit.Value;
        else
        {
            var row = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
            value = row?.Value;
            Cache[key] = (value, DateTime.UtcNow);
        }
        if (!string.IsNullOrWhiteSpace(value)) return value!;
        if (configKey.Length > 0) return _config[configKey] ?? "";
        return "";
    }

    public async Task SetAsync(string key, string value)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync();
        Cache[key] = (value, DateTime.UtcNow);   // S2.0.4: the new value at once
    }
}

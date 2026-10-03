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

    public async Task<string> GetAsync(string key, string configKey = "")
    {
        var row = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (row is not null && !string.IsNullOrWhiteSpace(row.Value)) return row.Value;
        if (configKey.Length > 0) return _config[configKey] ?? "";
        return "";
    }

    public async Task SetAsync(string key, string value)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync();
    }
}

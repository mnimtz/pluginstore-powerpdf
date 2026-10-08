namespace AddonStore.Web.Services;

/// <summary>
/// Which Power PDF installations may use the store (S1.7.0). The store client (1.5.0+) tells the
/// server how its Power PDF is licensed in the header X-License-Mode: "cloud" (Cloud License
/// Server, the SaaS edition), "server" (on-premise License Server), "serial" (legacy serial
/// number) or "unknown"; older clients send nothing and count as "unknown". Admins switch each
/// mode on or off under Settings, "Add-on Store". For a mode that is off, the catalog only lists
/// the store client itself (so every client can still update to a version that understands the
/// rule), downloads of add-ons are refused, and the client hides its ribbon button.
/// Requests that do not come from the store client (website, API, assistants) are not affected.
/// It is a usage rule, not copy protection: the client reports the mode itself.
/// </summary>
public static class StoreAccess
{
    public const string Header = "X-License-Mode";
    public const string SettingKey = "Store.LicenseModes";   // allowed modes, comma separated; "" = all (default), "none" = no mode
    public static readonly (string Key, string Label)[] Modes =
    {
        ("cloud", "Cloud License Server (SaaS)"),
        ("server", "License Server on premises"),
        ("serial", "Serial number"),
        ("unknown", "Unknown, or store clients before 1.5.0"),
    };

    public static string ModeOf(HttpContext ctx)
    {
        var m = ctx.Request.Headers[Header].ToString().Trim().ToLowerInvariant();
        return Modes.Any(x => x.Key == m) ? m : "unknown";
    }

    public static async Task<HashSet<string>> AllowedModesAsync(SettingsService settings)
    {
        var v = (await settings.GetAsync(SettingKey)).Trim();
        if (v.Length == 0) return Modes.Select(m => m.Key).ToHashSet();
        return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => Modes.Any(m => m.Key == k)).ToHashSet();
    }

    public static async Task SaveAsync(SettingsService settings, IEnumerable<string> allowed)
    {
        var keep = Modes.Select(m => m.Key).Where(allowed.Contains).ToList();
        await settings.SetAsync(SettingKey, keep.Count == Modes.Length ? "" : keep.Count == 0 ? "none" : string.Join(",", keep));
    }

    /// <summary>True unless the request comes from a store client whose license mode is switched off.</summary>
    public static async Task<bool> AllowedAsync(HttpContext ctx, SettingsService settings) =>
        UsageService.Classify(ctx).Source != "client" || (await AllowedModesAsync(settings)).Contains(ModeOf(ctx));
}

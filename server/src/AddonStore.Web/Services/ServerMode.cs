namespace AddonStore.Web.Services;

/// <summary>
/// S2.4.0: operating mode of the server, set by admins under Settings > Server ("Operating mode").
/// "production" is the store as it always was; "beta" marks the whole store as a beta: a red "Beta"
/// badge at the logo (web pages and, from the store client that reads it, the store window), a short
/// note on the start page, and a beta section in the disclaimer. Nothing is locked in beta.
/// The app setting <c>Server:Mode</c> wins over the stored value (an emergency switch while the portal
/// cannot be reached); further modes (maintenance, test) can be added here later.
/// </summary>
public static class ServerMode
{
    public const string Key = "Server.Mode";
    public const string ConfigKey = "Server:Mode";
    public const string Production = "production";
    public const string Beta = "beta";
    public static readonly string[] All = { Production, Beta };

    /// <summary>A known mode name (case and spaces ignored).</summary>
    public static bool IsKnown(string? value) => All.Contains((value ?? "").Trim().ToLowerInvariant());

    /// <summary>The mode for a stored or configured value; anything unknown or empty is production.</summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        return All.Contains(v) ? v : Production;
    }

    /// <summary>True when the app setting <c>Server:Mode</c> fixes the mode (the settings page then only shows it).</summary>
    public static bool IsForced(IConfiguration config) => IsKnown(config[ConfigKey]);

    /// <summary>The current mode: app setting first, then the stored value (SettingsService keeps it in memory for 30 s).</summary>
    public static async Task<string> GetAsync(SettingsService settings, IConfiguration config) =>
        IsForced(config) ? Normalize(config[ConfigKey]) : Normalize(await settings.GetAsync(Key));
}

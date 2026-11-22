using System.Globalization;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.4.0/S2.6.0: operating mode of the server, set by admins under Settings > Server ("Operating mode").
/// <list type="bullet">
/// <item>production: the store as it always was.</item>
/// <item>beta: a red "Beta" badge at the logo, a note on the start page, a beta section in the disclaimer; nothing locked.</item>
/// <item>maintenance: a yellow badge with a wrench and a notice on every page; changes pause for everybody except admins
/// (<see cref="MaintenanceGate"/>), the catalog, downloads and installations from Power PDF keep working.</item>
/// <item>test: a blue "Test" badge and a notice on every page, search engines kept out. A server marked as UAT test
/// system (FederationService.MarkedAsTest) counts as test while production is selected.</item>
/// </list>
/// The app setting <c>Server:Mode</c> wins over the stored value (an emergency switch while the portal cannot be reached).
/// </summary>
public static class ServerMode
{
    public const string Key = "Server.Mode";
    public const string ConfigKey = "Server:Mode";
    /// <summary>S2.6.0: optional expected end of a maintenance (UTC, round trip format) and a free note for the notice.</summary>
    public const string UntilKey = "Server.MaintenanceUntil";
    public const string NoteKey = "Server.ModeNote";
    public const int MaxNote = 300;
    public const string Production = "production";
    public const string Beta = "beta";
    public const string Maintenance = "maintenance";
    public const string Test = "test";
    public static readonly string[] All = { Production, Beta, Maintenance, Test };

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

    /// <summary>The stored or configured mode without the UAT marking (what the settings page selects).</summary>
    public static async Task<string> SelectedAsync(SettingsService settings, IConfiguration config) =>
        IsForced(config) ? Normalize(config[ConfigKey]) : Normalize(await settings.GetAsync(Key));

    /// <summary>The mode in force: app setting first, then the stored value; production on a marked UAT is test.</summary>
    public static async Task<string> GetAsync(SettingsService settings, IConfiguration config)
    {
        var mode = await SelectedAsync(settings, config);
        return mode == Production && FederationService.MarkedAsTest ? Test : mode;
    }

    /// <summary>The expected end of a maintenance, or null (none set, unreadable or already over).</summary>
    public static async Task<DateTime?> UntilAsync(SettingsService settings)
    {
        return ParseUntil(await settings.GetAsync(UntilKey), DateTime.UtcNow);
    }

    /// <summary>A stored end ("o" format, UTC) as UTC, or null when empty, unreadable or not after <paramref name="nowUtc"/>.</summary>
    public static DateTime? ParseUntil(string? raw, DateTime nowUtc) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u)
        && u > nowUtc ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : null;

    /// <summary>The admin's note for the notice (plain text, already capped).</summary>
    public static async Task<string> NoteAsync(SettingsService settings)
    {
        var n = (await settings.GetAsync(NoteKey)).Trim();
        return n.Length > MaxNote ? n[..MaxNote] : n;
    }

    /// <summary>A note as stored: control and text-direction characters removed, one line, at most <see cref="MaxNote"/> characters.</summary>
    public static string CleanNote(string? note)
    {
        var s = new string((note ?? "").Where(ch => !char.IsControl(ch) && ch is not ('‪' or '‫' or '‬' or '‭' or '‮'
            or '⁦' or '⁧' or '⁨' or '⁩')).ToArray()).Trim();
        return s.Length > MaxNote ? s[..MaxNote] : s;
    }
}

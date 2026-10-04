using System.Globalization;

namespace AddonStore.Web.Services;

/// <summary>
/// Converts stored UTC timestamps into the instance's display time zone
/// (admin setting "App.TimeZone", IANA id, default Europe/Berlin) and formats
/// them in the request culture.
/// </summary>
public class TimeDisplay
{
    public const string DefaultZone = "Europe/Berlin";

    /// <summary>Zones offered on the settings page (IANA ids work on Linux and Windows since .NET 6).</summary>
    public static readonly string[] Zones =
    {
        "UTC", "Europe/London", "Europe/Lisbon", "Europe/Dublin", "Europe/Berlin", "Europe/Paris",
        "Europe/Amsterdam", "Europe/Brussels", "Europe/Zurich", "Europe/Vienna", "Europe/Rome",
        "Europe/Madrid", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Warsaw",
        "Europe/Prague", "Europe/Budapest", "Europe/Helsinki", "Europe/Athens", "Europe/Istanbul",
        "Europe/Moscow", "America/New_York", "America/Chicago", "America/Los_Angeles",
        "Asia/Kolkata", "Asia/Singapore", "Asia/Tokyo", "Australia/Sydney"
    };

    private readonly SettingsService _settings;
    private TimeZoneInfo? _zone;

    public TimeDisplay(SettingsService settings) => _settings = settings;

    private TimeZoneInfo Zone()
    {
        if (_zone is not null) return _zone;
        var id = _settings.GetAsync("App.TimeZone", "App:TimeZone").GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(id)) id = DefaultZone;
        try { _zone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { _zone = TimeZoneInfo.Utc; }
        return _zone;
    }

    /// <summary>"MMM d, yyyy HH:mm" in local time; date only when <paramref name="withTime"/> is false.</summary>
    public string Fmt(DateTime utc, bool withTime = true)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone());
        return local.ToString(withTime ? "MMM d, yyyy HH:mm" : "MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public string Fmt(DateTime? utc, bool withTime = true) => utc is null ? "-" : Fmt(utc.Value, withTime);

    public string ZoneId => Zone().Id;
}

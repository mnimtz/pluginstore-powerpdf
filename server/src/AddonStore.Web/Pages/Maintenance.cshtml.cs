using AddonStore.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages;

/// <summary>
/// S2.6.0: explains the maintenance; web forms land here when the maintenance gate paused their change
/// (Services/MaintenanceGate.cs). Outside maintenance it returns to the start page.
/// </summary>
public class MaintenanceModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly IConfiguration _config;
    public DateTime? Until { get; private set; }
    public string Note { get; private set; } = "";

    public MaintenanceModel(SettingsService settings, IConfiguration config) { _settings = settings; _config = config; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await ServerMode.GetAsync(_settings, _config) != ServerMode.Maintenance) return LocalRedirect("/");
        Until = await ServerMode.UntilAsync(_settings);
        Note = await ServerMode.NoteAsync(_settings);
        Response.Headers.RetryAfter = MaintenanceGate.RetryAfterSeconds(Until, DateTime.UtcNow).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Page();
    }
}

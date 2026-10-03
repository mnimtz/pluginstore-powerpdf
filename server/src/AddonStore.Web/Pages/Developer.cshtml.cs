using AddonStore.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages;

public class DeveloperModel : PageModel
{
    private readonly SettingsService _settings;

    public string BaseUrl { get; private set; } = "";

    public DeveloperModel(SettingsService settings) => _settings = settings;

    public async Task OnGetAsync()
    {
        var configured = await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl");
        BaseUrl = string.IsNullOrWhiteSpace(configured)
            ? $"{Request.Scheme}://{Request.Host}"
            : configured.TrimEnd('/');
    }
}

using System.Globalization;
using AddonStore.Web.Services;
using AddonStore.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>
/// Public page of one add-on, /a/{slug} (S0.10.0): the link a sales person
/// sends to a customer. Shows only this add-on with install button, client
/// download and a share box; ?ref= attributes views and clicks in the reports.
/// </summary>
public class AddonModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly ShareService _share;

    public AddonModel(AppDbContext db, SettingsService settings, ShareService share)
    {
        _db = db; _settings = settings; _share = share;
    }

    public CatalogItem? Item { get; private set; }
    public string Slug { get; private set; } = "";
    public string Ref { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public string PageUrl { get; private set; } = "";
    public string? ClientVersion { get; private set; }
    public string? ClientDownloadUrl { get; private set; }
    public List<ScreenshotService.Shot> Shots { get; private set; } = new();
    public string ShotVersion { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string slug, [FromQuery(Name = "ref")] string? reference)
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var items = await CatalogUi.GetAsync(_db, culture, includeBeta: true);
        var client = items.FirstOrDefault(i => i.Id == SubmissionService.ClientPackageId);
        items = items.Where(i => i.Id != SubmissionService.ClientPackageId).ToList();

        var ids = items.Select(i => i.Id).ToList();
        var id = ShareService.Resolve(slug, ids);
        Item = id is null ? null : items.First(i => i.Id == id);
        if (Item is null)
        {
            Response.StatusCode = 404;
            return Page();
        }

        Slug = ShareService.Slug(Item.Id, ids);
        Ref = ShareService.CleanRef(reference);
        var configured = await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl");
        BaseUrl = !string.IsNullOrWhiteSpace(configured) ? configured.TrimEnd('/')
            : (Request.Host.Host is "localhost" or "127.0.0.1" ? Request.Scheme : "https") + "://" + Request.Host;
        PageUrl = $"{BaseUrl}/a/{Slug}";
        if (client is not null)
        {
            ClientVersion = client.Version;
            ClientDownloadUrl = "/download/pluginstore.msi" + (Ref.Length > 0 ? $"?pkg={Uri.EscapeDataString(Item.Id)}&ref={Ref}" : $"?pkg={Uri.EscapeDataString(Item.Id)}");
        }
        var shown = await ScreenshotService.DisplayVersionAsync(_db, Item.Id);
        if (shown is not null)
        {
            Shots = ScreenshotService.FromManifest(shown.ManifestJson, culture);
            ShotVersion = shown.Version;
        }
        if (!ShareService.IsPreviewBot(Request.Headers.UserAgent)) await _share.CountAsync(Item.Id, Ref, "view");
        return Page();
    }
}

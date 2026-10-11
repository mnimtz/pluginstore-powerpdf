using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AddonStore.Web.Pages.Admin;

/// <summary>
/// UAT environments (S1.21.0, docs/concepts/master-child.md). On a server that runs on its own:
/// "Add UAT" (one-time pairing code), the paired UATs, block and remove. On a UAT: the mode,
/// pairing with the production server, connection test and unpairing.
/// </summary>
public class UatModel : PageModel
{
    private readonly FederationService _svc;
    private readonly SettingsService _settings;
    private readonly UserManager<AppUser> _users;

    public UatModel(FederationService svc, SettingsService settings, UserManager<AppUser> users)
    {
        _svc = svc; _settings = settings; _users = users;
    }

    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    /// <summary>Error code and English detail of a refused step.</summary>
    public string? Detail { get; private set; }

    public FederationService.ChildState State { get; private set; } = null!;
    public List<FederationChild> Children { get; private set; } = new();
    public List<FederationService.CodeInfo> Codes { get; private set; } = new();
    public List<FederationIntake> Intakes { get; private set; } = new();
    public List<FederationHandover> Handovers { get; private set; } = new();
    public string OwnUrl { get; private set; } = "";
    /// <summary>The code just created: shown once.</summary>
    public (string Code, string Name, DateTime ExpiresAt)? NewCode { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    /// <summary>E1b: .reg for test PCs (to=uat sets ServerUrl and TrustedSigningKeys, to=production removes them).</summary>
    public async Task<IActionResult> OnGetRegAsync(string to)
    {
        var st = await _svc.ChildStateAsync();
        if (!st.IsChild || st.TrustedSigningKeys.Length == 0) return NotFound();
        var uat = to != "production";
        return File(FederationService.RegFile(uat, await OwnUrlAsync(), st.TrustedSigningKeys), "application/octet-stream",
                    uat ? "addon-store-use-uat.reg" : "addon-store-use-production.reg");
    }

    private async Task<string> AdminAsync() => (await _users.GetUserAsync(User))?.DisplayName ?? "admin";

    private async Task<IActionResult> DoneAsync(FedResult r, string okNotice)
    {
        if (r.Ok) Notice = okNotice;
        else
        {
            Notice = "The step was refused.";
            NoticeKind = "error";
            Detail = r.Code + ": " + r.Message;
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCodeAsync(string name)
    {
        var me = await _users.GetUserAsync(User);
        var r = await _svc.CreateCodeAsync(name, me?.DisplayName ?? "admin", me?.Id);
        if (r.Data is FederationService.CodeCreated c) NewCode = (c.Code, c.Name, c.ExpiresAt);
        return await DoneAsync(r, "Pairing code created.");
    }

    public async Task<IActionResult> OnPostRevokeAsync(string id) =>
        await DoneAsync(await _svc.RevokeCodeAsync(id, await AdminAsync()), "Pairing code revoked.");

    public async Task<IActionResult> OnPostBlockAsync(string id, bool blocked) =>
        await DoneAsync(await _svc.BlockChildAsync(id, blocked, await AdminAsync()), blocked ? "UAT blocked." : "UAT unblocked.");

    public async Task<IActionResult> OnPostRemoveAsync(string id) =>
        await DoneAsync(await _svc.RemoveChildAsync(id, await AdminAsync()), "UAT removed.");

    public async Task<IActionResult> OnPostPairAsync(string masterUrl, string code) =>
        await DoneAsync(await _svc.PairWithMasterAsync(masterUrl, code, await OwnUrlAsync(), await AdminAsync()), "Paired with the production server.");

    public async Task<IActionResult> OnPostTestAsync() =>
        await DoneAsync(await _svc.TestConnectionAsync(await AdminAsync()), "The connection to the production server works.");

    public async Task<IActionResult> OnPostUnpairAsync() =>
        await DoneAsync(await _svc.UnpairAsync(await AdminAsync()), "Pairing removed.");

    private async Task<string> OwnUrlAsync()
    {
        var own = (await _settings.GetAsync("App.PublicBaseUrl", "App:PublicBaseUrl")).TrimEnd('/');
        return own.Length > 0 ? own : $"{Request.Scheme}://{Request.Host}";
    }

    private async Task LoadAsync()
    {
        State = await _svc.ChildStateAsync();
        if (!State.IsChild)
        {
            Children = await _svc.ChildrenAsync();
            Codes = await _svc.OpenCodesAsync();
            Intakes = await _svc.IntakesAsync();
        }
        else Handovers = await _svc.HandoversAsync();
        OwnUrl = await OwnUrlAsync();
    }
}

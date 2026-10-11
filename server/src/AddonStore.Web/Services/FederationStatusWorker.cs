namespace AddonStore.Web.Services;

/// <summary>
/// UAT (S1.23.0, stage E3): every 15 minutes it asks production for the state of the handed-over
/// versions that are not final yet (submitted, beta). Nothing happens on a server that is no UAT.
/// </summary>
public class FederationStatusWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FederationStatusWorker> _log;

    public FederationStatusWorker(IServiceScopeFactory scopes, ILogger<FederationStatusWorker> log)
    {
        _scopes = scopes; _log = log;
    }

    /// <summary>E4 (S1.24.0): once a day, only when the UAT admin switched it on.</summary>
    private async Task MirrorDailyAsync(IServiceProvider sp, FederationService fed)
    {
        var settings = sp.GetRequiredService<SettingsService>();
        if (await settings.GetAsync(FederationService.MirrorDailyKey) != "true") return;
        var last = (await settings.GetAsync(FederationService.LastMirrorKey)).Split('|')[0];
        if (DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            && DateTime.UtcNow - at < TimeSpan.FromHours(23)) return;
        var users = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AddonStore.Web.Data.AppUser>>();
        var admin = (await users.GetUsersInRoleAsync("Admin")).Where(u => u.Status == AddonStore.Web.Data.UserStatus.Active)
            .OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (admin is null) return;
        var r = await fed.MirrorAsync(admin);
        _log.LogInformation("UAT daily mirror: {Result}", r.Ok ? "done" : r.Code);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            if (FederationService.IsChild)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var fed = scope.ServiceProvider.GetRequiredService<FederationService>();
                    var r = await fed.RefreshStatusAsync("UAT status check");
                    if (!r.Ok && r.Code != "FED_NOT_PAIRED") _log.LogInformation("UAT status check: {Code}", r.Code);
                    await MirrorDailyAsync(scope.ServiceProvider, fed);
                }
                catch (Exception e) { _log.LogWarning(e, "UAT status check failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        }
    }
}

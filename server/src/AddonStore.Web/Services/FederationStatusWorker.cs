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
                    var r = await scope.ServiceProvider.GetRequiredService<FederationService>().RefreshStatusAsync("UAT status check");
                    if (!r.Ok && r.Code != "FED_NOT_PAIRED") _log.LogInformation("UAT status check: {Code}", r.Code);
                }
                catch (Exception e) { _log.LogWarning(e, "UAT status check failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stop); } catch (OperationCanceledException) { return; }
        }
    }
}

using AddonStore.Web.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AddonStore.Web.Auth;

/// <summary>
/// The effective "Sign in with Microsoft" configuration of this process, changed at run time without a restart.
/// <para>Precedence: when any Entra:* app setting is set (<see cref="EntraSettings.HasAppSettings"/>), the app
/// settings decide for the whole lifetime of the process and the admin page shows them read-only. Otherwise the
/// values of the admin page (AppSettings table) apply; they are re-read at most every 30 seconds and at once after
/// a save on this server.</para>
/// <para>The OpenID Connect scheme is always registered; its options are built from <see cref="Current"/>
/// (<see cref="EntraSignIn"/>). When the configuration changes, the cached options (including the metadata
/// manager and its signing keys) are dropped, so the next request builds them from the new values.</para>
/// </summary>
public sealed class EntraRuntime
{
    // AppSettings keys of the admin page (Pages/Admin/Settings, section "entra")
    public const string EnabledKey = "Entra.Enabled";
    public const string ClientIdKey = "Entra.ClientId";
    /// <summary>The directory as entered: directory (tenant) id or verified domain.</summary>
    public const string TenantKey = "Entra.Tenant";
    /// <summary>The directory id the entry resolved to when it was saved.</summary>
    public const string TenantIdKey = "Entra.TenantId";
    /// <summary>Additional allowed directories as entered (comma separated ids or domains).</summary>
    public const string DirectoriesKey = "Entra.AllowedDirectories";
    /// <summary>The directory ids the additional directories resolved to.</summary>
    public const string DirectoryIdsKey = "Entra.AllowedTenantIds";
    /// <summary>The client secret, "dp:" + ASP.NET Data Protection (purpose <see cref="SecretPurpose"/>), like the Resend key.</summary>
    public const string SecretKey = "Entra.ClientSecret";
    public const string SecretPurpose = "AddonStore.EntraSecret";

    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private sealed record State(EntraSettings Settings, DateTime At);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _optionsCache;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile State _state;

    public EntraRuntime(IConfiguration config, IServiceScopeFactory scopes, IDataProtectionProvider dp,
                        IOptionsMonitorCache<OpenIdConnectOptions> optionsCache, ILogger<EntraRuntime> log)
    {
        _scopes = scopes; _protector = dp.CreateProtector(SecretPurpose); _optionsCache = optionsCache; _log = log;
        FromAppSettings = EntraSettings.HasAppSettings(config);
        _state = FromAppSettings
            ? new State(EntraSettings.From(config), DateTime.MaxValue)
            : new State(EntraSettings.Off, DateTime.MinValue);
    }

    /// <summary>True when the Entra:* app settings decide (the admin page is read-only then).</summary>
    public bool FromAppSettings { get; }

    /// <summary>The configuration last loaded; synchronous for the options setup. Pages use <see cref="GetAsync"/>.</summary>
    public EntraSettings Current => _state.Settings;

    /// <summary>The current configuration, re-read from the database when it is older than 30 seconds.</summary>
    public Task<EntraSettings> GetAsync()
    {
        var st = _state;
        return FromAppSettings || DateTime.UtcNow - st.At < MaxAge ? Task.FromResult(st.Settings) : ReloadAsync();
    }

    /// <summary>Reads the admin page values now (after a save, at start-up, after a restore).</summary>
    public async Task<EntraSettings> ReloadAsync()
    {
        if (FromAppSettings) return Current;
        await _gate.WaitAsync();
        try
        {
            using var scope = _scopes.CreateScope();
            var next = await ReadStoreAsync(scope.ServiceProvider.GetRequiredService<SettingsService>());
            Apply(next);
            return next;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // keep the last configuration; try again in 30 seconds
            _log.LogWarning(ex, "Sign in with Microsoft: the settings could not be read");
            _state = _state with { At = DateTime.UtcNow };
            return Current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The configuration stored by the admin page (the secret decrypted, never logged).</summary>
    public async Task<EntraSettings> ReadStoreAsync(SettingsService s) =>
        EntraSettings.FromStore(
            await s.GetAsync(EnabledKey) == "1",
            await s.GetAsync(ClientIdKey),
            UnprotectSecret(await s.GetAsync(SecretKey)),
            await s.GetAsync(TenantKey),
            await s.GetAsync(TenantIdKey),
            (await s.GetAsync(DirectoryIdsKey)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public string ProtectSecret(string plain) => "dp:" + _protector.Protect(plain);

    /// <summary>The plain secret, or "" when none is stored or it cannot be decrypted any more (other key ring).</summary>
    public string UnprotectSecret(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dp:", StringComparison.Ordinal)) return "";
        try { return _protector.Unprotect(stored[3..]); }
        catch (System.Security.Cryptography.CryptographicException) { return ""; }
    }

    /// <summary>Makes <paramref name="next"/> the current configuration; drops the cached scheme options when it differs.</summary>
    public void Apply(EntraSettings next)
    {
        var before = _state.Settings;
        _state = new State(next, DateTime.UtcNow);
        if (before.SameAs(next)) return;
        _optionsCache.TryRemove(EntraSettings.Scheme);
        if (next.Enabled) _log.LogInformation("Sign in with Microsoft on ({Source}), {Count} allowed tenant(s)", next.Source, next.AllowedTenants.Count);
        else if (next.Problem is not null) _log.LogWarning("Sign in with Microsoft stays off ({Source}): {Problem}", next.Source, next.Problem);
        else _log.LogInformation("Sign in with Microsoft off");
    }
}

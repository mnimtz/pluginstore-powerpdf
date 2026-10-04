using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services;

/// <summary>
/// Signs every catalog entry (ECDSA P-256, SHA-256) so the Power PDF client can
/// tell packages of THIS store from packages of any other server, even when the
/// server address in the user's profile was changed. Signed message:
/// "addonstore-pkg-v2\n{id}\n{version}\n{sha256 lowercase hex}\n{zxt name}".
/// When the key cannot be read (key ring lost after a restore), the catalog
/// keeps working unsigned (clients then refuse installs) and admins see why.
/// Key source, first hit wins: setting Signing:PrivateKeyPem (App Setting /
/// key vault, for recovery and rotation), then the database setting
/// Signing.PrivateKey (encrypted with the data protection key ring, created on
/// first use). The client pins the public keys it trusts.
/// </summary>
public sealed class PackageSigning
{
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const string MessagePrefix = "addonstore-pkg-v2";
    private const string SettingKey = "Signing.PrivateKey";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtectionProvider _dp;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private ECDsa? _key;
    private DateTime _retryAfter = DateTime.MinValue;
    private readonly ILogger<PackageSigning> _log;

    /// <summary>Why no key is available (shown to admins), null when signing works.</summary>
    public string? Problem { get; private set; }

    public string KeyId { get; private set; } = "";
    public string PublicKeyPem { get; private set; } = "";
    /// <summary>Raw public key X||Y (64 bytes, base64), the form the client pins.</summary>
    public string PublicKeyRaw { get; private set; } = "";

    public PackageSigning(IServiceScopeFactory scopes, IDataProtectionProvider dp, IConfiguration config, ILogger<PackageSigning> log)
    {
        _scopes = scopes; _dp = dp; _config = config; _log = log;
    }

    private async Task<ECDsa?> KeyAsync()
    {
        if (_key is not null) return _key;
        if (DateTime.UtcNow < _retryAfter) return null;
        await _gate.WaitAsync();
        try
        {
            if (_key is not null) return _key;
            return await LoadKeyAsync();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        {
            // Never create a new key here: clients pin the old one. An admin restores the
            // key ring (backup password) or sets Signing:PrivateKeyPem (recovery key).
            Problem = "The package signing key cannot be read (" + ex.GetType().Name + "). Restore the backup with its key ring " +
                      "password, or set the App Setting Signing__PrivateKeyPem to the recovery key. The catalog is served unsigned meanwhile, " +
                      "so clients refuse installs.";
            _log.LogError(ex, "Package signing key unavailable");
            _retryAfter = DateTime.UtcNow.AddMinutes(1);
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// True when this server could use the signing key stored in a backup's database
    /// (value of Signing.PrivateKey): the recovery key is configured, the backup has no
    /// key yet, or the current key ring decrypts it.
    /// </summary>
    public bool CanUseStoredKey(string? stored)
    {
        if (!string.IsNullOrWhiteSpace(_config["Signing:PrivateKeyPem"])) return true;
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dp:")) return true;
        try { _dp.CreateProtector("AddonStore.PackageSigning").Unprotect(stored[3..]); return true; }
        catch (CryptographicException) { return false; }
    }

    private async Task<ECDsa> LoadKeyAsync()
    {
        {
            var ec = ECDsa.Create();
            var pem = _config["Signing:PrivateKeyPem"];
            if (!string.IsNullOrWhiteSpace(pem))
                ec.ImportFromPem(pem.Replace("\\n", "\n"));
            else
            {
                using var scope = _scopes.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var protector = _dp.CreateProtector("AddonStore.PackageSigning");
                var stored = await settings.GetAsync(SettingKey);
                if (stored.StartsWith("dp:"))
                    ec.ImportFromPem(protector.Unprotect(stored[3..]));
                else
                {
                    ec.Dispose();
                    ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    await settings.SetAsync(SettingKey, "dp:" + protector.Protect(ec.ExportECPrivateKeyPem()));
                }
            }
            if (ec.KeySize != 256) throw new InvalidOperationException("Signing key must be ECDSA P-256.");
            var p = ec.ExportParameters(false);
            PublicKeyRaw = Convert.ToBase64String([.. p.Q.X!, .. p.Q.Y!]);
            PublicKeyPem = ec.ExportSubjectPublicKeyInfoPem();
            KeyId = Convert.ToHexString(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
            _key = ec;
            Problem = null;
            return ec;
        }
    }

    public async Task EnsureLoadedAsync() => await KeyAsync();

    /// <summary>"keyId:base64(r||s)" for one package version ("" when no key is available).</summary>
    public string Sign(string id, string version, string sha256, string zxtName)
    {
        var msg = $"{MessagePrefix}\n{id}\n{version}\n{sha256.ToLowerInvariant()}\n{zxtName}";
        if (_cache.TryGetValue(msg, out var hit)) return hit;
        var key = _key;
        if (key is null) return "";
        var sig = key.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var value = KeyId + ":" + Convert.ToBase64String(sig);
        _cache[msg] = value;
        return value;
    }
}

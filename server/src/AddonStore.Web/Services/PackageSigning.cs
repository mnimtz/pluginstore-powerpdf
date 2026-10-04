using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services;

/// <summary>
/// Signs every catalog entry (ECDSA P-256, SHA-256) so the Power PDF client can
/// tell packages of THIS store from packages of any other server, even when the
/// server address in the user's profile was changed. Signed message:
/// "addonstore-pkg-v1\n{id}\n{version}\n{sha256 lowercase hex}".
/// Key source, first hit wins: setting Signing:PrivateKeyPem (App Setting /
/// key vault, for recovery and rotation), then the database setting
/// Signing.PrivateKey (encrypted with the data protection key ring, created on
/// first use). The client pins the public keys it trusts.
/// </summary>
public sealed class PackageSigning
{
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const string MessagePrefix = "addonstore-pkg-v1";
    private const string SettingKey = "Signing.PrivateKey";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtectionProvider _dp;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private ECDsa? _key;

    public string KeyId { get; private set; } = "";
    public string PublicKeyPem { get; private set; } = "";
    /// <summary>Raw public key X||Y (64 bytes, base64), the form the client pins.</summary>
    public string PublicKeyRaw { get; private set; } = "";

    public PackageSigning(IServiceScopeFactory scopes, IDataProtectionProvider dp, IConfiguration config)
    {
        _scopes = scopes; _dp = dp; _config = config;
    }

    private async Task<ECDsa> KeyAsync()
    {
        if (_key is not null) return _key;
        await _gate.WaitAsync();
        try
        {
            if (_key is not null) return _key;
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
            return ec;
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureLoadedAsync() => await KeyAsync();

    /// <summary>"keyId:base64(r||s)" for one package version.</summary>
    public async Task<string> SignAsync(string id, string version, string sha256)
    {
        await KeyAsync();
        return Sign(id, version, sha256);
    }

    /// <summary>Same as <see cref="SignAsync"/>; call <see cref="EnsureLoadedAsync"/> first.</summary>
    public string Sign(string id, string version, string sha256)
    {
        var msg = $"{MessagePrefix}\n{id}\n{version}\n{sha256.ToLowerInvariant()}";
        if (_cache.TryGetValue(msg, out var hit)) return hit;
        var key = _key ?? throw new InvalidOperationException("Signing key not loaded.");
        var sig = key.SignData(Encoding.UTF8.GetBytes(msg), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var value = KeyId + ":" + Convert.ToBase64String(sig);
        _cache[msg] = value;
        return value;
    }
}

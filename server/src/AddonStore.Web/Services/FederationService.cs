using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AddonStore.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>Outcome of a federation step: ok with data, or an error code for the API and the portal.</summary>
public record FedResult(bool Ok, string Code = "", string Message = "", int Status = 200, object? Data = null)
{
    public static FedResult Done(object? data = null) => new(true, Data: data);
}

/// <summary>
/// UAT environments (docs/concepts/master-child.md, stage E1, S1.21.0).
///
/// A server runs on its own until an admin adds a UAT: then it is that UAT's master and accepts
/// pairing and signed requests from it; without a UAT (and without an open pairing code) the
/// federation addresses answer 404. Only the UAT has a setting: mode "child" plus the master
/// address. Pairing: the master admin creates a one-time code (15 minutes, stored as hash), the
/// UAT admin enters master address and code, the UAT sends its own ECDSA P-256 public key, the
/// master answers with its federation key and the pairing id. From then on every request of the
/// UAT is signed (method, path, time, nonce, SHA-256 of the body) and every answer of the master too.
/// The federation keys are separate from the package signing key and protected like it.
/// </summary>
public class FederationService
{
    public const string ModeKey = "Federation.Mode";                 // "" (on its own) or "child" (UAT)
    public const string MasterUrlKey = "Federation.MasterUrl";       // UAT: https address of its master
    public const string PairingIdKey = "Federation.PairingId";       // UAT: id the master gave
    public const string PairingNameKey = "Federation.PairingName";   // UAT: name the master gave
    public const string MasterPublicKeyKey = "Federation.MasterPublicKey";   // UAT: master federation key (SPKI, base64)
    public const string PairedAtKey = "Federation.PairedAt";
    public const string LastTestKey = "Federation.LastTest";         // UAT: "<utc iso>|ok" or "<utc iso>|<code>"
    public const string MasterSigningKeyIdKey = "Federation.MasterSigningKeyId";   // UAT: package signing key id of production (E1b)
    /// <summary>Where the store client reads its IT policies (HKLM); a test PC gets ServerUrl and TrustedSigningKeys there.</summary>
    public const string PolicyKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store";
    public const string OwnKeyKey = "Federation.PrivateKey";         // both sides: own federation key ("dp:" protected PEM)
    public const string HttpName = "federation";
    public const string SignPrefix = "AddonStore-Federation-v1";
    private const string Purpose = "AddonStore.Federation";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I

    /// <summary>Mode of this server, read at start and after every change (portal band, catalog "environment").</summary>
    public static volatile bool IsChild;

    // nonces of the last minutes per pairing: a request seen once is refused again (replay)
    private static readonly ConcurrentDictionary<string, DateTime> SeenNonces = new();

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly IDataProtectionProvider _dp;
    private readonly IHttpClientFactory _http;
    private readonly IHostEnvironment _env;
    private readonly AuditService _audit;
    private readonly PackageSigning _signing;

    public FederationService(AppDbContext db, SettingsService settings, IDataProtectionProvider dp, IHttpClientFactory http,
                             IHostEnvironment env, AuditService audit, PackageSigning signing)
    {
        _db = db; _settings = settings; _dp = dp; _http = http; _env = env; _audit = audit; _signing = signing;
    }

    private static FedResult Err(string code, string message, int status = 400) => new(false, code, message, status);

    public async Task InitAsync() => IsChild = await _settings.GetAsync(ModeKey) == "child";

    // ---- keys -------------------------------------------------------------------------------

    /// <summary>The own federation key; created on first use. Null when the stored key cannot be read (restored without key ring).</summary>
    private async Task<ECDsa?> OwnKeyAsync(bool create)
    {
        var protector = _dp.CreateProtector(Purpose);
        var stored = await _settings.GetAsync(OwnKeyKey);
        if (stored.StartsWith("dp:"))
        {
            try
            {
                var ec = ECDsa.Create();
                ec.ImportFromPem(protector.Unprotect(stored[3..]));
                return ec;
            }
            catch (CryptographicException) { return null; }
        }
        if (!create) return null;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _settings.SetAsync(OwnKeyKey, "dp:" + protector.Protect(key.ExportECPrivateKeyPem()));
        return key;
    }

    private async Task<ECDsa> NewOwnKeyAsync()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await _settings.SetAsync(OwnKeyKey, "dp:" + _dp.CreateProtector(Purpose).Protect(key.ExportECPrivateKeyPem()));
        return key;
    }

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static string KeyIdOf(string spkiBase64)
    {
        if (string.IsNullOrEmpty(spkiBase64)) return "";
        try { return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(spkiBase64)))[..16].ToLowerInvariant(); }
        catch (FormatException) { return ""; }
    }

    /// <summary>An ECDSA P-256 public key from SPKI base64, or null.</summary>
    public static ECDsa? ImportPublicKey(string spkiBase64)
    {
        if (string.IsNullOrEmpty(spkiBase64) || spkiBase64.Length > 400) return null;
        try
        {
            var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
            if (ec.KeySize != 256 || ec.ExportParameters(false).Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                ec.Dispose();
                return null;
            }
            return ec;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string RequestText(string pairingId, string method, string pathAndQuery, string timestamp, string nonce, string bodyHash) =>
        string.Join('\n', SignPrefix, pairingId, method.ToUpperInvariant(), pathAndQuery, timestamp, nonce, bodyHash);

    public static string ResponseText(string nonce, int status, string bodyHash) =>
        string.Join('\n', SignPrefix, "response", nonce, status.ToString(), bodyHash);

    private static string Sign(ECDsa key, string text) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static bool Verify(ECDsa key, string text, string signatureBase64)
    {
        try
        {
            var sig = Convert.FromBase64String(signatureBase64);
            return sig.Length == 64 && key.VerifyData(Encoding.UTF8.GetBytes(text), sig, HashAlgorithmName.SHA256,
                                                     DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException) { return false; }
    }

    // ---- codes --------------------------------------------------------------------------------

    /// <summary>"ABCDE-FGHJK-LMNPQ-RSTUV": 20 characters of 32 (100 bits), grouped for reading aloud.</summary>
    private static string NewCode()
    {
        var b = RandomNumberGenerator.GetBytes(20);
        var sb = new StringBuilder(23);
        for (var i = 0; i < 20; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(CodeAlphabet[b[i] & 31]);
        }
        return sb.ToString();
    }

    public static string NormalizeCode(string code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string CodeHash(string code) => Sha256Hex(Encoding.UTF8.GetBytes("federation-code\n" + NormalizeCode(code)));

    // ---- master side --------------------------------------------------------------------------

    /// <summary>Does this server take federation requests at all (a paired UAT or an open code)?</summary>
    public async Task<bool> MasterActiveAsync()
    {
        if (IsChild) return false;
        var now = DateTime.UtcNow;
        return await _db.FederationChildren.AnyAsync() ||
               await _db.FederationPairingCodes.AnyAsync(c => c.UsedAt == null && c.ExpiresAt > now);
    }

    public record CodeInfo(string Id, string Name, DateTime CreatedAt, string CreatedBy, DateTime ExpiresAt);
    public record CodeCreated(string Id, string Name, string Code, DateTime ExpiresAt);

    public async Task<List<FederationChild>> ChildrenAsync() =>
        await _db.FederationChildren.OrderBy(c => c.CreatedAt).ToListAsync();

    public async Task<List<CodeInfo>> OpenCodesAsync()
    {
        var now = DateTime.UtcNow;
        return await _db.FederationPairingCodes.Where(c => c.UsedAt == null && c.ExpiresAt > now).OrderBy(c => c.CreatedAt)
            .Select(c => new CodeInfo(c.Id, c.Name, c.CreatedAt, c.CreatedBy, c.ExpiresAt)).ToListAsync();
    }

    /// <summary>"Add UAT": a one-time pairing code (shown once; only its hash is kept).</summary>
    public async Task<FedResult> CreateCodeAsync(string name, string by)
    {
        if (IsChild) return Err("FED_IS_UAT", "This server is a UAT itself; only a server that runs on its own adds a UAT.", 409);
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            return Err("FED_NAME_INVALID", "The name of the UAT needs 1 to 60 characters without control characters.");
        var now = DateTime.UtcNow;
        // expired and used codes are of no use any more
        await _db.FederationPairingCodes.Where(c => c.UsedAt == null && c.ExpiresAt <= now).ExecuteDeleteAsync();
        if (await _db.FederationPairingCodes.CountAsync(c => c.UsedAt == null) >= 5)
            return Err("FED_TOO_MANY_CODES", "There are 5 open pairing codes already; revoke one or let them expire.", 409);
        if (await OwnKeyAsync(create: true) is null)
            return Err("FED_KEY_UNREADABLE", "The federation key of this server cannot be read (restored without its key ring).", 500);
        var code = NewCode();
        var row = new FederationPairingCode { Name = name, CodeHash = CodeHash(code), CreatedBy = by, CreatedAt = now, ExpiresAt = now + CodeLifetime };
        _db.FederationPairingCodes.Add(row);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(by, "federation.code.created", name, $"code {row.Id}, valid until {row.ExpiresAt:u}");
        return FedResult.Done(new CodeCreated(row.Id, name, code, row.ExpiresAt));
    }

    public async Task<FedResult> RevokeCodeAsync(string id, string by)
    {
        var row = await _db.FederationPairingCodes.FirstOrDefaultAsync(c => c.Id == id && c.UsedAt == null);
        if (row is null) return Err("FED_CODE_NOT_FOUND", "No open pairing code with this id.", 404);
        _db.FederationPairingCodes.Remove(row);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(by, "federation.code.revoked", row.Name, $"code {row.Id}");
        return FedResult.Done(new { id, revoked = true });
    }

    public async Task<FedResult> BlockChildAsync(string id, bool blocked, string by)
    {
        var c = await _db.FederationChildren.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Err("FED_UAT_NOT_FOUND", "No paired UAT with this id.", 404);
        c.BlockedAt = blocked ? DateTime.UtcNow : null;
        c.BlockedBy = blocked ? by : null;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(by, blocked ? "federation.uat.blocked" : "federation.uat.unblocked", c.Name, $"pairing {c.Id}");
        return FedResult.Done(new { id, blocked });
    }

    public async Task<FedResult> RemoveChildAsync(string id, string by)
    {
        var c = await _db.FederationChildren.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return Err("FED_UAT_NOT_FOUND", "No paired UAT with this id.", 404);
        _db.FederationChildren.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(by, "federation.uat.removed", c.Name, $"pairing {c.Id}");
        return FedResult.Done(new { id, removed = true });
    }

    /// <summary>POST /api/federation/pair: a UAT presents a code and its public key.</summary>
    public async Task<FedResult> PairAsync(string code, string childPublicKey, string childUrl, string fromIp)
    {
        var norm = NormalizeCode(code);
        if (norm.Length != 20) return Err("FED_CODE_INVALID", "The pairing code is wrong, used or expired. Create a new one on the production server.", 403);
        var now = DateTime.UtcNow;
        var hash = CodeHash(norm);
        var row = await _db.FederationPairingCodes.FirstOrDefaultAsync(c => c.CodeHash == hash);
        if (row is null || row.UsedAt is not null || row.ExpiresAt <= now)
            return Err("FED_CODE_INVALID", "The pairing code is wrong, used or expired. Create a new one on the production server.", 403);
        using var childKey = ImportPublicKey(childPublicKey);
        if (childKey is null) return Err("FED_KEY_INVALID", "publicKey must be an ECDSA P-256 public key (SubjectPublicKeyInfo, base64).");
        using var own = await OwnKeyAsync(create: true);
        if (own is null) return Err("FED_KEY_UNREADABLE", "The federation key of this server cannot be read (restored without its key ring).", 500);
        childUrl = (childUrl ?? "").Trim();
        if (childUrl.Length > 300 || !Uri.TryCreate(childUrl, UriKind.Absolute, out var cu) || cu.Scheme is not ("https" or "http")) childUrl = "";
        string id;
        do id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        while (await _db.FederationChildren.AnyAsync(c => c.Id == id));
        // one step: two requests with the same code at the same moment cannot both pair
        var taken = await _db.FederationPairingCodes.Where(c => c.Id == row.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now).SetProperty(c => c.ChildId, id));
        if (taken != 1) return Err("FED_CODE_INVALID", "The pairing code is wrong, used or expired. Create a new one on the production server.", 403);
        var child = new FederationChild
        {
            Id = id, Name = row.Name, PublicKey = PublicKeyOf(childKey), ChildUrl = childUrl,
            CreatedAt = now, CreatedBy = row.CreatedBy, LastSeenAt = now,
        };
        _db.FederationChildren.Add(child);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("UAT " + row.Name, "federation.uat.paired", row.Name, $"pairing {id}, key {KeyIdOf(child.PublicKey)}, from {fromIp}" + (childUrl.Length > 0 ? ", " + childUrl : ""));
        var master = PublicKeyOf(own);
        // E1b: the UAT warns when it signs packages with the same key as production
        await _signing.EnsureLoadedAsync();
        return FedResult.Done(new { pairingId = id, name = row.Name, masterPublicKey = master, masterKeyId = KeyIdOf(master), childKeyId = KeyIdOf(child.PublicKey),
                                    signingKeyId = _signing.KeyId });
    }

    /// <summary>Checks a signed request of a paired UAT; the body is the exact bytes received.</summary>
    public async Task<(FederationChild? Child, FedResult? Error)> VerifyRequestAsync(HttpRequest req, byte[] body)
    {
        string H(string name) => req.Headers[name].ToString();
        var id = H("X-Federation-Id");
        var ts = H("X-Federation-Timestamp");
        var nonce = H("X-Federation-Nonce");
        var sig = H("X-Federation-Signature");
        if (id.Length is < 1 or > 40 || ts.Length is < 1 or > 20 || nonce.Length is < 16 or > 64 || sig.Length is < 1 or > 200 ||
            !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return (null, Err("FED_SIGNATURE_INVALID", "A federation request needs the headers X-Federation-Id, -Timestamp, -Nonce and -Signature.", 401));
        var child = await _db.FederationChildren.FirstOrDefaultAsync(c => c.Id == id);
        if (child is null) return (null, Err("FED_SIGNATURE_INVALID", "Unknown pairing or wrong signature.", 401));
        if (!long.TryParse(ts, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix))
            return (null, Err("FED_TIMESTAMP", "X-Federation-Timestamp is the Unix time in seconds.", 401));
        var at = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unix, 0, 253402300799)).UtcDateTime;
        var now = DateTime.UtcNow;
        if ((now - at).Duration() > MaxClockSkew)
            return (null, Err("FED_TIMESTAMP", "The request is older than 5 minutes or the clocks differ; check the time of both servers.", 401));
        using var key = ImportPublicKey(child.PublicKey);
        var text = RequestText(child.Id, req.Method, req.Path.Value + req.QueryString.Value, ts, nonce, Sha256Hex(body));
        if (key is null || !Verify(key, text, sig)) return (null, Err("FED_SIGNATURE_INVALID", "Unknown pairing or wrong signature.", 401));
        // only now, so nobody can fill the list with unsigned requests
        if (SeenNonces.Count > 50000)
            foreach (var k in SeenNonces.Where(x => now - x.Value > MaxClockSkew * 2).Select(x => x.Key).ToList()) SeenNonces.TryRemove(k, out _);
        if (!SeenNonces.TryAdd(child.Id + "|" + nonce, now)) return (null, Err("FED_REPLAY", "This request was received before.", 401));
        if (child.BlockedAt is not null) return (null, Err("FED_BLOCKED", "The production server blocked this UAT.", 403));
        if (child.LastSeenAt is null || now - child.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            child.LastSeenAt = now;
            await _db.SaveChangesAsync();
        }
        return (child, null);
    }

    /// <summary>Signature of an answer to a signed request (header X-Federation-Signature).</summary>
    public async Task<string> SignResponseAsync(string nonce, int status, byte[] body)
    {
        using var own = await OwnKeyAsync(create: false);
        return own is null ? "" : Sign(own, ResponseText(nonce, status, Sha256Hex(body)));
    }

    // ---- UAT side -----------------------------------------------------------------------------

    public record ChildState(bool IsChild, string MasterUrl, string PairingId, string Name, string MasterKeyId, string OwnKeyId,
                             DateTime? PairedAt, string LastTest, bool KeyProblem, string SigningKeyId = "", string TrustedSigningKeys = "",
                             bool SameSigningKey = false);

    public async Task<ChildState> ChildStateAsync()
    {
        var pairingId = await _settings.GetAsync(PairingIdKey);
        var master = await _settings.GetAsync(MasterPublicKeyKey);
        string own = "";
        var problem = false;
        if (pairingId.Length > 0)
        {
            using var k = await OwnKeyAsync(create: false);
            if (k is null) problem = true; else own = KeyIdOf(PublicKeyOf(k));
        }
        DateTime? paired = DateTime.TryParse(await _settings.GetAsync(PairedAtKey), System.Globalization.CultureInfo.InvariantCulture,
                                             System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        await _signing.EnsureLoadedAsync();
        var signingId = _signing.KeyId;
        var masterSigning = await _settings.GetAsync(MasterSigningKeyIdKey);
        return new ChildState(IsChild, await _settings.GetAsync(MasterUrlKey), pairingId, await _settings.GetAsync(PairingNameKey),
                              KeyIdOf(master), own, paired, await _settings.GetAsync(LastTestKey), problem,
                              signingId, signingId.Length > 0 && _signing.PublicKeyRaw is { Length: > 0 } raw ? signingId + ":" + raw : "",
                              masterSigning.Length > 0 && masterSigning == signingId);
    }

    /// <summary>
    /// E1b: a .reg file for test PCs. "uat" sets ServerUrl and TrustedSigningKeys (this UAT's address
    /// and package signing key), "production" removes both again. UTF-16 LE with BOM, as regedit writes it.
    /// </summary>
    public static byte[] RegFile(bool toUat, string serverUrl, string trustedSigningKeys)
    {
        static string Q(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var sb = new StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        sb.Append("; Add-on Store for Power PDF: ").Append(toUat ? "this PC uses the UAT store " + serverUrl : "this PC uses production again").Append("\r\n");
        sb.Append('[').Append(PolicyKey).Append("]\r\n");
        if (toUat)
        {
            sb.Append("\"ServerUrl\"=\"").Append(Q(serverUrl)).Append("\"\r\n");
            sb.Append("\"TrustedSigningKeys\"=\"").Append(Q(trustedSigningKeys)).Append("\"\r\n");
        }
        else
        {
            sb.Append("\"ServerUrl\"=-\r\n");
            sb.Append("\"TrustedSigningKeys\"=-\r\n");
        }
        return [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(sb.ToString())];
    }

    /// <summary>Switches between "on its own" and "UAT (child)". Leaving UAT mode forgets the pairing.</summary>
    public async Task<FedResult> SetModeAsync(bool child, string by)
    {
        if (child == IsChild) return FedResult.Done(new { mode = child ? "child" : "standalone" });
        if (child && await _db.FederationChildren.AnyAsync())
            return Err("FED_HAS_UAT", "This server is the master of a UAT; remove its UATs before it becomes a UAT itself.", 409);
        if (!child) await ForgetPairingAsync();
        await _settings.SetAsync(ModeKey, child ? "child" : "");
        IsChild = child;
        await _audit.LogAsync(by, "settings.changed", ModeKey, child ? "child" : "standalone");
        return FedResult.Done(new { mode = child ? "child" : "standalone" });
    }

    private async Task ForgetPairingAsync()
    {
        foreach (var k in new[] { PairingIdKey, PairingNameKey, MasterPublicKeyKey, PairedAtKey, LastTestKey, OwnKeyKey, MasterSigningKeyIdKey })
            await _settings.SetAsync(k, "");
    }

    public async Task<FedResult> UnpairAsync(string by)
    {
        var id = await _settings.GetAsync(PairingIdKey);
        if (id.Length == 0) return Err("FED_NOT_PAIRED", "This UAT is not paired.", 409);
        await ForgetPairingAsync();
        await _audit.LogAsync(by, "federation.unpaired", await _settings.GetAsync(MasterUrlKey), $"pairing {id}");
        return FedResult.Done(new { unpaired = true });
    }

    /// <summary>The master address an admin typed: https (http only for localhost in Development), no user info, query or fragment.</summary>
    public string? CleanMasterUrl(string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.Length is < 8 or > 200 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0) return null;
        var local = u.IsLoopback;
        if (u.Scheme != "https" && !(u.Scheme == "http" && local && _env.IsDevelopment())) return null;
        return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>UAT: pairs with the master (address and code from the master admin).</summary>
    public async Task<FedResult> PairWithMasterAsync(string masterUrl, string code, string ownUrl, string by)
    {
        if (!IsChild) return Err("FED_NOT_UAT", "Switch this server to the mode UAT (child) first.", 409);
        var url = CleanMasterUrl(masterUrl);
        if (url is null) return Err("FED_URL_INVALID", "The address of the production server must start with https:// and contain no user, query or fragment.");
        if (NormalizeCode(code).Length != 20) return Err("FED_CODE_INVALID", "The pairing code has 20 characters (groups of 5).", 400);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { code = NormalizeCode(code), publicKey = PublicKeyOf(key), url = ownUrl });
        var (status, json, _, error) = await SendAsync(HttpMethod.Post, url + "/api/federation/pair", body, null);
        if (error is not null) return error;
        if (status == 404 && json is null)
            return Err("FED_MASTER_NOT_READY", "The production server takes no pairing right now; create a new code there (UAT environments, Add UAT).", 502);
        if (json is not { } j || !j.TryGetProperty("ok", out var ok))
            return Err("FED_MASTER_UNREACHABLE", $"The production server gave no valid answer (HTTP {status}).", 502);
        if (ok.ValueKind != JsonValueKind.True)
        {
            var c = j.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var ec) ? ec.GetString() ?? "" : "";
            return status == 404
                ? Err("FED_MASTER_NOT_READY", "The production server takes no pairing right now; create a new code there (UAT environments, Add UAT).", 502)
                : Err(c.StartsWith("FED_") || c == "RATE_LIMITED" ? c : "FED_PAIR_FAILED", "The production server refused the pairing" + (c.Length > 0 ? $" ({c})." : "."), 502);
        }
        var d = j.GetProperty("data");
        string S(string n) => d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var pairingId = S("pairingId");
        var masterKey = S("masterPublicKey");
        using var mk = ImportPublicKey(masterKey);
        if (mk is null || pairingId.Length is < 1 or > 40 || !pairingId.All(char.IsAsciiLetterOrDigit))
            return Err("FED_MASTER_UNREACHABLE", "The production server sent an invalid pairing answer.", 502);
        // the new key replaces any earlier pairing of this UAT
        await _settings.SetAsync(OwnKeyKey, "dp:" + _dp.CreateProtector(Purpose).Protect(key.ExportECPrivateKeyPem()));
        await _settings.SetAsync(MasterUrlKey, url);
        await _settings.SetAsync(PairingIdKey, pairingId);
        await _settings.SetAsync(PairingNameKey, S("name"));
        await _settings.SetAsync(MasterPublicKeyKey, masterKey);
        await _settings.SetAsync(MasterSigningKeyIdKey, S("signingKeyId"));
        await _settings.SetAsync(PairedAtKey, DateTime.UtcNow.ToString("o"));
        await _settings.SetAsync(LastTestKey, DateTime.UtcNow.ToString("o") + "|ok");
        await _audit.LogAsync(by, "federation.paired", url, $"pairing {pairingId}, master key {KeyIdOf(masterKey)}");
        return FedResult.Done(new { pairingId, name = S("name"), masterUrl = url, masterKeyId = KeyIdOf(masterKey) });
    }

    /// <summary>UAT: a signed GET /api/federation/ping; checks pairing, clocks and the master's answer signature.</summary>
    public async Task<FedResult> TestConnectionAsync(string by)
    {
        var url = await _settings.GetAsync(MasterUrlKey);
        var id = await _settings.GetAsync(PairingIdKey);
        if (!IsChild || id.Length == 0 || url.Length == 0) return Err("FED_NOT_PAIRED", "This UAT is not paired.", 409);
        using var own = await OwnKeyAsync(create: false);
        if (own is null) return Err("FED_KEY_UNREADABLE", "The federation key of this UAT cannot be read (restored without its key ring); pair again.", 500);
        var (status, json, _, error) = await SendAsync(HttpMethod.Get, url + "/api/federation/ping", Array.Empty<byte>(), (id, own));
        FedResult r;
        if (error is not null) r = error;
        else if (json is { } j && j.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            r = FedResult.Done(j.GetProperty("data"));
        else
        {
            var c = json is { } k && k.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var ec) ? ec.GetString() ?? "" : "";
            r = status == 404 ? Err("FED_NOT_PAIRED", "The production server does not know this UAT any more; pair again.", 502)
                              : Err(c.StartsWith("FED_") ? c : "FED_MASTER_UNREACHABLE", $"The production server refused the request ({(c.Length > 0 ? c : "HTTP " + status)}).", 502);
        }
        await _settings.SetAsync(LastTestKey, DateTime.UtcNow.ToString("o") + "|" + (r.Ok ? "ok" : r.Code));
        await _audit.LogAsync(by, "federation.tested", url, r.Ok ? "ok" : r.Code);
        return r;
    }

    /// <summary>A request to the master: no redirects, 20 s, at most 64 KB answer; signed (and the answer checked) when a key is given.</summary>
    private async Task<(int Status, JsonElement? Json, string Raw, FedResult? Error)> SendAsync(HttpMethod method, string url, byte[] body, (string Id, ECDsa Key)? signer)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body.Length > 0 || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        string nonce = "";
        if (signer is { } s)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
            var uri = new Uri(url);
            var text = RequestText(s.Id, method.Method, uri.AbsolutePath + uri.Query, ts, nonce, Sha256Hex(body));
            req.Headers.Add("X-Federation-Id", s.Id);
            req.Headers.Add("X-Federation-Timestamp", ts);
            req.Headers.Add("X-Federation-Nonce", nonce);
            req.Headers.Add("X-Federation-Signature", Sign(s.Key, text));
        }
        HttpResponseMessage resp;
        try { resp = await _http.CreateClient(HttpName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (0, null, "", Err("FED_MASTER_UNREACHABLE", "The production server cannot be reached: " + e.GetType().Name + ".", 502));
        }
        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (status is >= 300 and < 400)
                return (status, null, "", Err("FED_MASTER_UNREACHABLE", $"The production server answered with a redirect (HTTP {status}); enter its final address.", 502));
            var buf = new byte[65536];
            var n = 0;
            await using (var st = await resp.Content.ReadAsStreamAsync())
            {
                int r;
                while (n < buf.Length && (r = await st.ReadAsync(buf.AsMemory(n))) > 0) n += r;
                if (n == buf.Length && await st.ReadAsync(new byte[1]) > 0)
                    return (status, null, "", Err("FED_MASTER_UNREACHABLE", "The answer of the production server is too large.", 502));
            }
            var bytes = buf.AsSpan(0, n).ToArray();
            if (signer is not null && status == 200)
            {
                using var mk = ImportPublicKey(await _settings.GetAsync(MasterPublicKeyKey));
                var sig = resp.Headers.TryGetValues("X-Federation-Signature", out var v) ? v.FirstOrDefault() ?? "" : "";
                if (mk is null || !Verify(mk, ResponseText(nonce, status, Sha256Hex(bytes)), sig))
                    return (status, null, "", Err("FED_ANSWER_UNSIGNED", "The answer of the production server is not signed with its federation key; pair again.", 502));
            }
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                return (status, doc.RootElement.Clone(), Encoding.UTF8.GetString(bytes), null);
            }
            catch (JsonException) { return (status, null, "", null); }
        }
    }
}

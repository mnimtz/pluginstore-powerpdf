using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection;

namespace AddonStore.Web.Services;

/// <summary>
/// Document storage of "Senden an": the encrypted blocks until the recipient
/// collects them. Only ciphertext is stored; blob names are random ids.
/// Azure Blob Storage (connection string or SAS URL, like the cloud backup);
/// in Development a local folder outside the backup (data/sendto-dev).
/// </summary>
public sealed class SendToStorage
{
    public const string AccessKey = "SendTo.Storage.Access";       // dp:-protected connection string or SAS URL
    public const string ContainerKey = "SendTo.Storage.Container";
    public const string LocalAccess = "local";                     // Development only

    private readonly SettingsService _settings;
    private readonly IDataProtector _protector;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;

    public SendToStorage(SettingsService settings, IDataProtectionProvider dp, IWebHostEnvironment env, IConfiguration config)
    {
        _settings = settings;
        _protector = dp.CreateProtector("AddonStore.SendToStorage");
        _env = env;
        _config = config;
    }

    public async Task<string> ContainerNameAsync()
    {
        var c = await _settings.GetAsync(ContainerKey, "SendTo:Container");
        return string.IsNullOrWhiteSpace(c) ? "sendto" : c.Trim();
    }

    /// <summary>The configured access (unprotected), "" if none.</summary>
    private async Task<string> AccessAsync()
    {
        var raw = await _settings.GetAsync(AccessKey, "SendTo:Storage");
        if (raw.StartsWith("dp:", StringComparison.Ordinal))
        {
            try { return _protector.Unprotect(raw[3..]); } catch { return ""; }   // keys of another instance
        }
        return raw;
    }

    public async Task<bool> IsConfiguredAsync() => (await AccessAsync()).Length > 0;

    /// <summary>Kind shown in the portal: none, local, connection, sas.</summary>
    public async Task<string> KindAsync()
    {
        var a = await AccessAsync();
        if (a.Length == 0) return "none";
        if (a == LocalAccess) return "local";
        return a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "sas" : "connection";
    }

    public async Task SaveAccessAsync(string access, string container)
    {
        access = access.Trim();
        if (access.Length > 0)
            await _settings.SetAsync(AccessKey, access == LocalAccess ? LocalAccess : "dp:" + _protector.Protect(access));
        if (!string.IsNullOrWhiteSpace(container)) await _settings.SetAsync(ContainerKey, container.Trim().ToLowerInvariant());
    }

    public async Task ClearAccessAsync() => await _settings.SetAsync(AccessKey, "");

    // ------------------------------------------------------------------ blocks

    private static string BlobName(string transferId, int n) => $"{transferId}/{n}";

    private string LocalRoot()
    {
        var data = _config["Storage:Data"];
        if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(_env.ContentRootPath, "data");
        return Path.Combine(data, "sendto-dev");
    }

    private async Task<BlobContainerClient?> ContainerAsync()
    {
        var access = await AccessAsync();
        if (access.Length == 0 || access == LocalAccess) return null;
        var name = await ContainerNameAsync();
        var options = new BlobClientOptions();
        if (access.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(access);
            // A SAS URL either points at the container or at the account.
            var containerInUrl = uri.AbsolutePath.Trim('/').Length > 0;
            return containerInUrl ? new BlobContainerClient(uri, options)
                                  : new BlobServiceClient(uri, options).GetBlobContainerClient(name);
        }
        return new BlobServiceClient(access, options).GetBlobContainerClient(name);
    }

    private async Task<bool> UseLocalAsync() => _env.IsDevelopment() && await AccessAsync() == LocalAccess;

    public async Task PutAsync(string transferId, int n, byte[] data, CancellationToken ct = default)
    {
        if (await UseLocalAsync())
        {
            var p = Path.Combine(LocalRoot(), transferId, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllBytesAsync(p, data, ct);
            return;
        }
        var c = await ContainerAsync() ?? throw new InvalidOperationException("SENDTO_STORAGE_MISSING");
        await c.GetBlobClient(BlobName(transferId, n)).UploadAsync(new BinaryData(data), overwrite: true, ct);
    }

    public async Task<byte[]?> GetAsync(string transferId, int n, CancellationToken ct = default)
    {
        if (await UseLocalAsync())
        {
            var p = Path.Combine(LocalRoot(), transferId, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return File.Exists(p) ? await File.ReadAllBytesAsync(p, ct) : null;
        }
        var c = await ContainerAsync();
        if (c is null) return null;
        try
        {
            var r = await c.GetBlobClient(BlobName(transferId, n)).DownloadContentAsync(ct);
            return r.Value.Content.ToArray();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    /// <summary>Deletes every block of a transfer (no soft delete on this container: gone for good).</summary>
    public async Task DeleteTransferAsync(string transferId, CancellationToken ct = default)
    {
        if (await UseLocalAsync())
        {
            try { Directory.Delete(Path.Combine(LocalRoot(), transferId), true); } catch (DirectoryNotFoundException) { }
            return;
        }
        var c = await ContainerAsync();
        if (c is null) return;
        await foreach (var b in c.GetBlobsAsync(BlobTraits.None, BlobStates.None, transferId + "/", ct))
            await c.DeleteBlobIfExistsAsync(b.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
    }

    /// <summary>
    /// Deletes blocks no open transfer knows (after a restore: transfers are not in the
    /// backup, their blocks stay behind without keys). Only blocks older than
    /// <paramref name="before"/>, so an upload that is just starting is never touched.
    /// </summary>
    public async Task<int> DeleteOrphansAsync(ISet<string> openTransfers, DateTime before, CancellationToken ct = default)
    {
        var n = 0;
        if (await UseLocalAsync())
        {
            var root = LocalRoot();
            if (!Directory.Exists(root)) return 0;
            foreach (var d in Directory.GetDirectories(root))
            {
                if (openTransfers.Contains(Path.GetFileName(d)) || Directory.GetLastWriteTimeUtc(d) >= before) continue;
                try { Directory.Delete(d, true); n++; } catch (IOException) { }
            }
            return n;
        }
        var c = await ContainerAsync();
        if (c is null) return 0;
        await foreach (var b in c.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, ct))
        {
            var slash = b.Name.IndexOf('/');
            var id = slash > 0 ? b.Name[..slash] : b.Name;
            if (openTransfers.Contains(id) || (b.Properties.LastModified?.UtcDateTime ?? DateTime.UtcNow) >= before) continue;
            await c.DeleteBlobIfExistsAsync(b.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
            n++;
        }
        return n;
    }

    /// <summary>Used bytes and blob count (portal overview).</summary>
    public async Task<(long bytes, int blobs)> UsageAsync(CancellationToken ct = default)
    {
        if (await UseLocalAsync())
        {
            var root = LocalRoot();
            if (!Directory.Exists(root)) return (0, 0);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            return (files.Sum(f => new FileInfo(f).Length), files.Length);
        }
        var c = await ContainerAsync();
        if (c is null) return (0, 0);
        long bytes = 0;
        int n = 0;
        try
        {
            await foreach (var b in c.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, ct)) { bytes += b.Properties.ContentLength ?? 0; n++; }
        }
        catch (RequestFailedException) { }
        return (bytes, n);
    }

    /// <summary>
    /// "Test connection": write, read, delete a test blob; soft delete and
    /// versioning must be off; no public access. Returns (ok, findings).
    /// </summary>
    public async Task<(bool ok, List<(string level, string text)> findings)> TestAsync(CancellationToken ct = default)
    {
        var f = new List<(string, string)>();
        var access = await AccessAsync();
        if (access.Length == 0) { f.Add(("error", "No document storage configured.")); return (false, f); }
        if (access == LocalAccess)
        {
            if (!_env.IsDevelopment()) { f.Add(("error", "The local folder is only allowed on a development server.")); return (false, f); }
            f.Add(("warn", "Local folder (development only), no Blob Storage."));
            return (true, f);
        }
        try
        {
            var c = (await ContainerAsync())!;
            await c.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            var probe = c.GetBlobClient("probe/" + Guid.NewGuid().ToString("N"));
            await probe.UploadAsync(new BinaryData(new byte[] { 1, 2, 3 }), overwrite: true, ct);
            var back = await probe.DownloadContentAsync(ct);
            if (back.Value.Content.ToArray().Length != 3) f.Add(("error", "Read back failed."));
            await probe.DeleteIfExistsAsync(cancellationToken: ct);
            f.Add(("ok", "Write, read and delete work."));
            try
            {
                var props = await c.GetPropertiesAsync(cancellationToken: ct);
                if (props.Value.PublicAccess != PublicAccessType.None) f.Add(("error", "The container allows public access."));
                else f.Add(("ok", "No public access."));
            }
            catch (RequestFailedException) { f.Add(("warn", "Public access could not be checked with this access.")); }
            try
            {
                var svc = Azure.Storage.Blobs.Specialized.SpecializedBlobExtensions.GetParentBlobServiceClient(c);
                var sp = await svc.GetPropertiesAsync(ct);
                if (sp.Value.DeleteRetentionPolicy?.Enabled == true)
                    f.Add(("error", "Soft delete for blobs is on: deleted documents would stay recoverable. Switch it off for this account."));
                else f.Add(("ok", "Soft delete for blobs is off."));
            }
            catch (Exception) { f.Add(("warn", "Soft delete could not be checked with this access (needs account-level rights); check it in the Azure portal.")); }
            f.Add(("warn", "Versioning and the lifecycle rule (delete after 31 days) cannot be read through the data API; check them in the Azure portal."));
        }
        catch (Exception e)
        {
            f.Add(("error", "Storage not reachable: " + e.Message));
        }
        return (!f.Any(x => x.Item1 == "error"), f);
    }
}

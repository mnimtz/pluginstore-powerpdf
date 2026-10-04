using System.IO.Compression;
using System.Text.Json;
using AddonStore.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// Full backup and restore of an instance: SQLite database (consistent online
/// snapshot), plugin packages, avatars and developer kit, plus backup.json
/// with metadata. Restore validates the archive, refuses to lock the acting
/// admin out and always writes a safety backup of the current state first.
/// </summary>
public class BackupService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly Api.AppVersion _version;

    public BackupService(AppDbContext db, IConfiguration config, IWebHostEnvironment env, Api.AppVersion version)
    {
        _db = db; _config = config; _env = env; _version = version;
    }

    public string DataRoot
    {
        get
        {
            var d = _config["Storage:Data"];
            return string.IsNullOrWhiteSpace(d) ? Path.Combine(_env.ContentRootPath, "data") : d;
        }
    }

    private string PackagesRoot
    {
        get
        {
            var r = _config["Storage:Root"];
            return string.IsNullOrWhiteSpace(r) ? Path.Combine(DataRoot, "packages") : r;
        }
    }

    private string DevkitRoot
    {
        get
        {
            var r = _config["Storage:Devkit"];
            return string.IsNullOrWhiteSpace(r) ? Path.Combine(DataRoot, "devkit") : r;
        }
    }

    private string AvatarsRoot => Path.Combine(DataRoot, "avatars");
    // Data protection key ring (Program.cs): decrypts stored secrets such as the AI API key.
    private string KeysRoot => Path.Combine(DataRoot, "keys");
    public string SafetyRoot => Path.Combine(DataRoot, "backups");

    private string DbPath =>
        new SqliteConnectionStringBuilder(_db.Database.GetConnectionString()).DataSource;

    /// <summary>Writes a full backup ZIP to <paramref name="target"/>.</summary>
    public async Task CreateAsync(string target, string createdBy)
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), "pluginstore-snap-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Online snapshot through SQLite's backup API: consistent even while
            // the app keeps writing.
            using (var src = new SqliteConnection(_db.Database.GetConnectionString()))
            using (var dst = new SqliteConnection($"Data Source={tmpDb}"))
            {
                src.Open(); dst.Open();
                src.BackupDatabase(dst);
            }
            SqliteConnection.ClearAllPools();

            var meta = new
            {
                format = 1,
                product = "PluginStore-PowerPDF",
                serverVersion = _version.Value,
                createdUtc = DateTime.UtcNow,
                createdBy,
                users = await _db.Users.CountAsync(),
                packages = await _db.Packages.CountAsync(),
                versions = await _db.PackageVersions.CountAsync(),
                sourceArchives = await _db.PackageVersions.CountAsync(v => v.SourcePath != null),
                categories = await _db.Categories.CountAsync(),
                usageCounterRows = await _db.UsageStats.CountAsync(),
                ipEvents = await _db.UsageEvents.CountAsync(),
                shareCounterRows = await _db.ShareStats.CountAsync(),
                // Everything the server keeps lives in pluginstore.db (users, roles, tokens, settings,
                // categories, catalog entries, audit) and these folders: packages (incl. *.source.zip),
                // avatars, devkit, keys. New data must land in one of them, or be added here.
                // Not backed up on purpose: data/geo (DB-IP databases, downloaded again by GeoService).
                contents = new[] { "pluginstore.db", "packages/ (packages and source code)", "avatars/", "devkit/",
                                   "keys/ (data protection key ring for encrypted settings such as the AI key)" }
            };

            await using var fs = File.Create(target);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(tmpDb, "pluginstore.db", CompressionLevel.Optimal);
            AddFolder(zip, PackagesRoot, "packages");
            AddFolder(zip, AvatarsRoot, "avatars");
            AddFolder(zip, DevkitRoot, "devkit");
            AddFolder(zip, KeysRoot, "keys");
            var e = zip.CreateEntry("backup.json");
            await using var es = e.Open();
            await JsonSerializer.SerializeAsync(es, meta, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            try { File.Delete(tmpDb); } catch { /* best effort */ }
        }
    }

    private static void AddFolder(ZipArchive zip, string root, string prefix)
    {
        if (!Directory.Exists(root)) return;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            zip.CreateEntryFromFile(f, $"{prefix}/{rel}", CompressionLevel.Fastest);
        }
    }

    public record RestoreCheck(bool Ok, string Message, JsonElement? Meta);

    /// <summary>Validates an uploaded archive without changing anything.</summary>
    public RestoreCheck Inspect(string zipPath, string actingUserId)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var n = entry.FullName.Replace('\\', '/');
                if (n.Contains("..") || n.StartsWith('/') || n.Contains(':'))
                    return new(false, "The archive contains unsafe paths.", null);
            }
            var metaEntry = zip.GetEntry("backup.json");
            var dbEntry = zip.GetEntry("pluginstore.db");
            if (metaEntry is null || dbEntry is null)
                return new(false, "This is not a Add-on Store backup (backup.json or pluginstore.db missing).", null);

            JsonElement meta;
            using (var ms = new MemoryStream())
            {
                using (var es = metaEntry.Open()) es.CopyTo(ms);
                meta = JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
            }
            if (!meta.TryGetProperty("product", out var prod) || prod.GetString() != "PluginStore-PowerPDF")
                return new(false, "This is not a Add-on Store backup.", meta);

            // Self-lockout protection: the acting admin must exist as an active
            // admin in the backup, otherwise nobody could sign in afterwards.
            var tmpDb = Path.Combine(Path.GetTempPath(), "pluginstore-check-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                dbEntry.ExtractToFile(tmpDb);
                using var c = new SqliteConnection($"Data Source={tmpDb};Mode=ReadOnly");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*) FROM AspNetUsers u JOIN AspNetUserRoles ur ON ur.UserId = u.Id " +
                    "JOIN AspNetRoles r ON r.Id = ur.RoleId WHERE u.Id = $id AND r.Name = 'Admin' AND u.Status = 1";
                cmd.Parameters.AddWithValue("$id", actingUserId);
                var n = Convert.ToInt32(cmd.ExecuteScalar());
                if (n == 0)
                    return new(false, "Your account is not an active admin in this backup; restoring it would lock you out.", meta);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { File.Delete(tmpDb); } catch { }
            }
            return new(true, "ok", meta);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or SqliteException)
        {
            return new(false, "The archive could not be read: " + ex.Message, null);
        }
    }

    /// <summary>
    /// Replaces database and files with the archive's content. A safety backup
    /// of the current state is written to data/backups first.
    /// </summary>
    public async Task<string> RestoreAsync(string zipPath, string actingName)
    {
        Directory.CreateDirectory(SafetyRoot);
        var safety = Path.Combine(SafetyRoot, $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        await CreateAsync(safety, actingName + " (automatic, before restore)");

        var dbPath = DbPath;
        using var zip = ZipFile.OpenRead(zipPath);

        // Database: replace the file under closed connections.
        SqliteConnection.ClearAllPools();
        var staged = dbPath + ".restore";
        zip.GetEntry("pluginstore.db")!.ExtractToFile(staged, overwrite: true);
        foreach (var side in new[] { dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(side)) File.Delete(side);
        File.Copy(staged, dbPath, overwrite: true);
        File.Delete(staged);
        SqliteConnection.ClearAllPools();

        ReplaceFolder(zip, "packages/", PackagesRoot);
        ReplaceFolder(zip, "avatars/", AvatarsRoot);
        ReplaceFolder(zip, "devkit/", DevkitRoot);
        AddMissingKeys(zip, KeysRoot);
        return safety;
    }

    /// <summary>
    /// Adds the backup's data protection keys to the key ring without removing
    /// or overwriting current ones, so sign-in cookies stay valid and secrets
    /// encrypted on the old server (AI key) can be read after the next restart.
    /// </summary>
    private static void AddMissingKeys(ZipArchive zip, string root)
    {
        Directory.CreateDirectory(root);
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("keys/") && e.Length > 0 && e.Length < 64 * 1024))
        {
            var name = e.FullName["keys/".Length..];
            if (name.Contains('/') || !name.StartsWith("key-") || !name.EndsWith(".xml")) continue;
            var target = Path.Combine(root, name);
            if (!File.Exists(target)) e.ExtractToFile(target);
        }
    }

    private static void ReplaceFolder(ZipArchive zip, string prefix, string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix) && e.Length > 0))
        {
            var rel = e.FullName[prefix.Length..];
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }
}

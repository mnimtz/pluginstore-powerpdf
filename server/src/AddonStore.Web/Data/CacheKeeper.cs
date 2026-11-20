using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Data;

/// <summary>
/// S2.0.5: keeps SQLite's shared page cache alive. With Cache=Shared all connections of the process share one cache,
/// but SQLite frees it when the last connection closes, and Microsoft.Data.Sqlite's pool closes idle connections
/// after a while (its prune timer). After every short pause the next request then started cold, and on the data
/// share every page costs about 50 ms (a list page 7 to 17 s). One connection held open for the life of the process
/// keeps the cache; it is released around a restore, which replaces the database file. After the start the main
/// list queries run once in the background, so the first visitor does not wait for a cold cache.
/// </summary>
public sealed class CacheKeeper : BackgroundService
{
    private static readonly object Gate = new();
    private static SqliteConnection? _held;
    private static string? _connectionString;

    private readonly IServiceProvider _services;
    private readonly ILogger<CacheKeeper> _log;

    public CacheKeeper(IServiceProvider services, ILogger<CacheKeeper> log) { _services = services; _log = log; }

    /// <summary>Opens the held connection (again); safe to call more than once.</summary>
    public static void Hold()
    {
        lock (Gate)
        {
            if (_held is not null || _connectionString is null) return;
            var c = new SqliteConnection(_connectionString);
            c.Open();
            _held = c;
        }
    }

    /// <summary>Closes the held connection (before the database file is replaced by a restore).</summary>
    public static void Release()
    {
        lock (Gate)
        {
            _held?.Dispose();
            _held = null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();   // nothing before Kestrel starts (see the start-up rules)
        try
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cs = db.Database.GetConnectionString();
                // only for a shared cache: without Cache=Shared a held connection would only keep its own cache
                if (cs is null || !cs.Contains("Cache=Shared", StringComparison.OrdinalIgnoreCase)) return;
                _connectionString = cs;
            }
            Hold();
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // what the list pages read: the light version rows, packages, accounts, reports, categories
                await db.PackageVersions.AsNoTracking().Select(PackageVersion.Light).ToListAsync(stop);
                await db.Packages.AsNoTracking().ToListAsync(stop);
                await db.Users.AsNoTracking().ToListAsync(stop);
                await db.Feedbacks.AsNoTracking().Select(f => new { f.Id, f.PackageId, f.Status }).ToListAsync(stop);
                await db.Categories.AsNoTracking().ToListAsync(stop);
                await db.AppSettings.AsNoTracking().ToListAsync(stop);
            }
            _log.LogInformation("Startup: page cache warmed in {Ms} ms", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Startup: page cache could not be warmed"); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Release();
    }
}

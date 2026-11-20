using System.Collections.Concurrent;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// S2.0.4: usage counters, download counters and (when switched on) IP events are collected in memory and written
/// once a minute in one transaction. On the data share every single write cost 0.2 to 2 seconds (journal file,
/// flush, lock) and blocked every reader meanwhile; a store client's catalog request wrote one counter each time.
/// Reports and download figures lag at most a minute; a crash loses at most that minute of counters.
/// </summary>
public sealed class UsageBuffer
{
    public sealed record StatKey(string Day, string Kind, string PackageId, string Version, string Source, string Lang, string ClientVersion,
                                 string Country, string Region, string City, string Org, string Arch, string HostVersion, string OsVersion);

    private ConcurrentDictionary<StatKey, int> _stats = new();
    private ConcurrentDictionary<int, int> _downloads = new();
    private ConcurrentQueue<UsageEvent> _events = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public void Count(StatKey key) => _stats.AddOrUpdate(key, 1, (_, n) => n + 1);
    public void Download(int versionId) => _downloads.AddOrUpdate(versionId, 1, (_, n) => n + 1);
    public void Event(UsageEvent e) { if (_events.Count < 50_000) _events.Enqueue(e); }

    public bool IsEmpty => _stats.IsEmpty && _downloads.IsEmpty && _events.IsEmpty;

    /// <summary>Writes everything collected so far in one transaction; on failure the numbers stay for the next try.</summary>
    public async Task FlushAsync(IServiceProvider services, ILogger log, CancellationToken ct = default)
    {
        if (IsEmpty) return;
        await _flushLock.WaitAsync(ct);
        try
        {
            var stats = Interlocked.Exchange(ref _stats, new ConcurrentDictionary<StatKey, int>());
            var downloads = Interlocked.Exchange(ref _downloads, new ConcurrentDictionary<int, int>());
            var events = new List<UsageEvent>();
            while (events.Count < 5000 && _events.TryDequeue(out var e)) events.Add(e);
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                foreach (var (k, n) in stats)
                    await db.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT INTO UsageStats (Day, Kind, PackageId, Version, Source, Lang, ClientVersion,
                                                Country, Region, City, Org, Arch, HostVersion, OsVersion, Count)
                        VALUES ({k.Day}, {k.Kind}, {k.PackageId}, {k.Version}, {k.Source}, {k.Lang}, {k.ClientVersion},
                                {k.Country}, {k.Region}, {k.City}, {k.Org}, {k.Arch}, {k.HostVersion}, {k.OsVersion}, {n})
                        ON CONFLICT (Day, Kind, PackageId, Version, Source, Lang, ClientVersion,
                                     Country, Region, City, Org, Arch, HostVersion, OsVersion)
                        DO UPDATE SET Count = Count + {n}", ct);
                foreach (var (id, n) in downloads)
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE PackageVersions SET Downloads = Downloads + {n} WHERE Id = {id}", ct);
                if (events.Count > 0) { db.UsageEvents.AddRange(events); await db.SaveChangesAsync(ct); }
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // keep the numbers for the next try (the events go back to the queue)
                foreach (var (k, n) in stats) _stats.AddOrUpdate(k, n, (_, m) => m + n);
                foreach (var (id, n) in downloads) _downloads.AddOrUpdate(id, n, (_, m) => m + n);
                foreach (var e in events) { e.Id = 0; _events.Enqueue(e); }
                log.LogWarning(ex, "Usage counters could not be written; trying again in a minute");
            }
        }
        finally { _flushLock.Release(); }
    }
}

/// <summary>S2.0.4: writes the <see cref="UsageBuffer"/> once a minute and when the server stops.</summary>
public sealed class UsageBufferWorker : BackgroundService
{
    private readonly UsageBuffer _buffer;
    private readonly IServiceProvider _services;
    private readonly ILogger<UsageBufferWorker> _log;

    public UsageBufferWorker(UsageBuffer buffer, IServiceProvider services, ILogger<UsageBufferWorker> log)
    { _buffer = buffer; _services = services; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Yield();   // nothing before Kestrel starts (see the start-up rules)
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stop); } catch (TaskCanceledException) { break; }
            await _buffer.FlushAsync(_services, _log, stop);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await _buffer.FlushAsync(_services, _log, CancellationToken.None); }   // the last minute on shutdown
        catch (Exception ex) { _log.LogWarning(ex, "Usage counters could not be written on shutdown"); }
    }
}

using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AddonStore.Web.Data;

/// <summary>
/// S2.0.2: where does a request spend its time? Database commands of the current request are counted and
/// timed (including reading their rows, which on the data share is the expensive part); every response
/// carries "Server-Timing: db;dur=...;desc=\"n queries\", app;dur=..." and a request slower than
/// Storage:SlowRequestMs (default 2000) is logged as a warning with its path, the database share of the time
/// and the slowest command (SQL text only, never parameter values).
/// </summary>
public sealed class RequestTiming
{
    private static readonly AsyncLocal<RequestTiming?> CurrentSlot = new();
    public static RequestTiming? Current => CurrentSlot.Value;

    private long _ticks;
    private int _count;
    private long _slowTicks;
    private string? _slowSql;
    private readonly object _lock = new();

    public int Count => _count;
    public double DbMs => TimeSpan.FromTicks(Interlocked.Read(ref _ticks)).TotalMilliseconds;
    public double SlowestMs => TimeSpan.FromTicks(Interlocked.Read(ref _slowTicks)).TotalMilliseconds;
    public string? SlowestSql { get { lock (_lock) return _slowSql; } }

    public static RequestTiming Begin() { var t = new RequestTiming(); CurrentSlot.Value = t; return t; }

    internal void Add(TimeSpan duration, string? sql)
    {
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _ticks, duration.Ticks);
        lock (_lock)
        {
            if (duration.Ticks <= _slowTicks) return;
            _slowTicks = duration.Ticks;
            _slowSql = sql is null ? null : (sql.Length > 300 ? sql[..300] + "..." : sql).Replace('\n', ' ').Replace('\r', ' ');
        }
    }

    /// <summary>S2.0.4: one slow request as admins see it (path without query string, never user data).</summary>
    public sealed record Slow(DateTime At, string Method, string Path, int Status, long TotalMs, double DbMs, int Queries, double SlowestMs, string? SlowestSql);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Slow> Recent = new();

    /// <summary>The last (at most 300) requests that took 500 ms or more, newest first.</summary>
    public static List<Slow> RecentSlow() => Recent.Reverse().ToList();

    /// <summary>The middleware: header on every response, a warning for slow requests.</summary>
    public static async Task Middleware(HttpContext ctx, Func<Task> next, ILogger log, int slowMs)
    {
        var t = Begin();
        var sw = Stopwatch.StartNew();
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers["Server-Timing"] =
                $"db;dur={t.DbMs:0};desc=\"{t.Count} queries\", app;dur={sw.Elapsed.TotalMilliseconds:0}";
            return Task.CompletedTask;
        });
        try { await next(); }
        finally
        {
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 500)
            {
                Recent.Enqueue(new Slow(DateTime.UtcNow, ctx.Request.Method, ctx.Request.Path.Value ?? "", ctx.Response.StatusCode,
                                        sw.ElapsedMilliseconds, Math.Round(t.DbMs), t.Count, Math.Round(t.SlowestMs), t.SlowestSql));
                while (Recent.Count > 300) Recent.TryDequeue(out _);
            }
            if (sw.ElapsedMilliseconds >= slowMs)
                log.LogWarning("Slow request {Method} {Path} -> {Status}: {TotalMs} ms, database {DbMs:0} ms in {Count} commands, slowest {SlowMs:0} ms: {Sql}",
                    ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode, sw.ElapsedMilliseconds, t.DbMs, t.Count, t.SlowestMs, t.SlowestSql ?? "-");
        }
    }
}

/// <summary>S2.0.2: feeds <see cref="RequestTiming"/> (a reader counts until it is disposed, so row reading is included).</summary>
public sealed class RequestTimingInterceptor : DbCommandInterceptor
{
    // EF calls this for synchronous and asynchronous readers alike (there is no async variant)
    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    { RequestTiming.Current?.Add(eventData.Duration, command.CommandText); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { RequestTiming.Current?.Add(eventData.Duration, command.CommandText); return ValueTask.FromResult(result); }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { RequestTiming.Current?.Add(eventData.Duration, command.CommandText); return result; }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    { RequestTiming.Current?.Add(eventData.Duration, command.CommandText); return ValueTask.FromResult(result); }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { RequestTiming.Current?.Add(eventData.Duration, command.CommandText); return result; }
}

/// <summary>S2.0.2: list pages load versions without their large review columns.</summary>
public static class LightVersions
{
    /// <summary>
    /// Fills <c>Versions</c> of the given packages with <see cref="PackageVersion.Light"/> rows. Only for packages
    /// loaded with AsNoTracking and pages that do not save them (the rows are not tracked).
    /// </summary>
    public static async Task AttachAsync(AppDbContext db, IReadOnlyCollection<Package> packages)
    {
        if (packages.Count == 0) return;
        var ids = packages.Select(p => p.Id).ToList();
        var rows = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.PackageVersions.Where(v => ids.Contains(v.PackageId)).Select(PackageVersion.Light))).ToLookup(v => v.PackageId);
        foreach (var p in packages) p.Versions = rows[p.Id].ToList();
    }
}

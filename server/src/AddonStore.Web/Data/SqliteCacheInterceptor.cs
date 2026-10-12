using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AddonStore.Web.Data;

/// <summary>
/// S1.25.2: a larger SQLite page cache. The database lives on the Azure Files share (/data, SMB); with
/// SQLite's default of 2 MB every catalog build and every list page read the version rows (with their
/// review reports) from the share again, 10 to 20 seconds per page once the data had grown. The connection
/// string uses Cache=Shared and pooled connections, so all connections of the process share this cache and
/// it stays warm between requests (only this process writes the file). No memory mapping: not on a share.
/// Setting Storage:SqliteCacheMb (default 128, 0 = SQLite's default).
/// </summary>
public sealed class SqliteCacheInterceptor : DbConnectionInterceptor
{
    private readonly string? _pragma;

    public SqliteCacheInterceptor(int megabytes) =>
        _pragma = megabytes > 0 ? $"PRAGMA cache_size=-{Math.Min(megabytes, 2048) * 1024}" : null;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (_pragma is null) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = _pragma;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
                                                     CancellationToken cancellationToken = default)
    {
        if (_pragma is null) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = _pragma;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

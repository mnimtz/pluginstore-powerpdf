using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Data;

/// <summary>
/// S2.0.2: column order of PackageVersions. SQLite stores a row as one record; to read a column it walks the
/// overflow pages of every column before it. The review reports (tens to hundreds of KB per version) sat in the
/// middle of the row, and every column added later (Downloads, Source*, Blocked*, AiReview*, Origin*) behind
/// them, so even the "light" projection read every report of every version over the network share (6 to 8 s
/// for a catalog or the categories, over a minute after sign-in). This step rebuilds the table once with the
/// small columns first, then Changelog and ManifestJson, then the large reports; a later ALTER ... ADD COLUMN
/// lands at the end and is moved forward at the next start. Idempotent, in one transaction, verified.
/// </summary>
public static class VersionTableLayout
{
    private const string Table = "PackageVersions";
    private const string Temp = "PackageVersions_layout";
    /// <summary>Medium-sized texts the light projection still reads: after the small columns.</summary>
    private static readonly string[] Middle = { "Changelog", "ManifestJson" };
    /// <summary>Large reports: at the very end, never needed by list queries.</summary>
    private static readonly string[] Large =
        { "ValidationReportJson", "RulesSnapshotJson", "ApprovalJson", "SourceReportJson", "AiReviewJson", "AiReviewTranslationsJson", "OriginReviewJson" };

    private sealed record Col(string Name, string Type, bool NotNull, string? Default, int Pk);

    /// <summary>The wanted order for the given current order (small ones keep their relative order).</summary>
    public static List<string> Desired(IReadOnlyList<string> current)
    {
        var small = current.Where(c => !Middle.Contains(c) && !Large.Contains(c)).ToList();
        if (small.Remove("Id")) small.Insert(0, "Id");
        return small.Concat(Middle.Where(current.Contains)).Concat(Large.Where(current.Contains)).ToList();
    }

    public static void Ensure(AppDbContext db, ILogger log) => Ensure(db.Database.GetDbConnection(), log);

    /// <summary>The same on a plain connection (tests).</summary>
    public static void Ensure(DbConnection conn, ILogger log)
    {
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var cols = Columns(conn);
        if (cols.Count == 0) return;
        var names = cols.Select(c => c.Name).ToList();
        var want = Desired(names);
        if (names.SequenceEqual(want)) return;

        // only the shape this code knows: the primary key Id, one foreign key to Packages, no triggers
        var pks = cols.Where(c => c.Pk > 0).Select(c => c.Name).ToList();
        var fks = Rows(conn, $"SELECT \"table\", \"from\", \"to\", on_delete FROM pragma_foreign_key_list('{Table}')");
        var triggers = Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = '{Table}'");
        if (!pks.SequenceEqual(new[] { "Id" }) || fks.Count != 1 || fks[0][0] != "Packages" || fks[0][1] != "PackageId" ||
            fks[0][2] != "Id" || !string.Equals(fks[0][3], "CASCADE", StringComparison.OrdinalIgnoreCase) || triggers != 0)
        {
            log.LogWarning("Startup: {Table} has an unexpected shape; column order left as it is", Table);
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        log.LogInformation("Startup: rebuilding {Table} with the large reports at the end ({Count} rows)", Table, Scalar(conn, $"SELECT COUNT(*) FROM \"{Table}\""));
        var indexes = Rows(conn, $"SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = '{Table}' AND sql IS NOT NULL").Select(r => r[0]).ToList();
        var byName = cols.ToDictionary(c => c.Name);
        string Def(Col c) => c.Name == "Id"
            ? "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_PackageVersions\" PRIMARY KEY AUTOINCREMENT"
            : $"\"{c.Name}\" {(string.IsNullOrWhiteSpace(c.Type) ? "TEXT" : c.Type)} {(c.NotNull ? "NOT NULL" : "NULL")}" +
              (c.Default is null ? "" : $" DEFAULT {c.Default}");
        var list = string.Join(", ", want.Select(n => $"\"{n}\""));
        var create = $"CREATE TABLE \"{Temp}\" (\n    " + string.Join(",\n    ", want.Select(n => Def(byName[n]))) +
                     ",\n    CONSTRAINT \"FK_PackageVersions_Packages_PackageId\" FOREIGN KEY (\"PackageId\") REFERENCES \"Packages\" (\"Id\") ON DELETE CASCADE\n)";
        var before = Fingerprint(conn, Table, want);

        Exec(conn, null, "PRAGMA foreign_keys = OFF");
        try
        {
            using var tx = conn.BeginTransaction();
            var seq = Rows(conn, $"SELECT seq FROM sqlite_sequence WHERE name = '{Table}'", tx).Select(r => long.Parse(r[0])).DefaultIfEmpty(0).First();
            Exec(conn, tx, $"DROP TABLE IF EXISTS \"{Temp}\"");
            Exec(conn, tx, create);
            Exec(conn, tx, $"INSERT INTO \"{Temp}\" ({list}) SELECT {list} FROM \"{Table}\"");
            Exec(conn, tx, $"DROP TABLE \"{Table}\"");
            Exec(conn, tx, $"ALTER TABLE \"{Temp}\" RENAME TO \"{Table}\"");
            foreach (var sql in indexes) Exec(conn, tx, sql);
            // AUTOINCREMENT: never hand out an Id again that a deleted version once had
            Exec(conn, tx, $"DELETE FROM sqlite_sequence WHERE name = '{Temp}'");
            var max = Rows(conn, $"SELECT COALESCE(MAX(Id), 0) FROM \"{Table}\"", tx).Select(r => long.Parse(r[0])).First();
            Exec(conn, tx, $"DELETE FROM sqlite_sequence WHERE name = '{Table}'");
            Exec(conn, tx, $"INSERT INTO sqlite_sequence (name, seq) VALUES ('{Table}', {Math.Max(seq, max)})");
            if (Rows(conn, $"SELECT * FROM pragma_foreign_key_check('{Table}')", tx).Count != 0)
                throw new InvalidOperationException("foreign key check failed after the rebuild");
            var after = Fingerprint(conn, Table, want, tx);
            if (after != before) throw new InvalidOperationException("content differs after the rebuild");
            if (!Columns(conn, tx).Select(c => c.Name).SequenceEqual(want)) throw new InvalidOperationException("column order not as wanted");
            tx.Commit();
            log.LogInformation("Startup: {Table} rebuilt in {Ms} ms", Table, clock.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Startup: rebuilding {Table} failed; nothing was changed", Table);
        }
        finally { Exec(conn, null, "PRAGMA foreign_keys = ON"); }
    }

    private static List<Col> Columns(DbConnection conn, DbTransaction? tx = null) =>
        Rows(conn, $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_xinfo('{Table}') WHERE hidden = 0 ORDER BY cid", tx, raw: true)
            .Select(r => new Col(r[0] ?? "", r[1] ?? "", r[2] == "1", r[3], int.Parse(r[4] ?? "0"))).ToList();

    /// <summary>Row count plus a hash over every column of every row, in Id order (the same before and after).</summary>
    private static string Fingerprint(DbConnection conn, string table, List<string> cols, DbTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(c => $"\"{c}\""))} FROM \"{table}\" ORDER BY Id";
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long n = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            n++;
            for (var i = 0; i < r.FieldCount; i++)
            {
                var v = r.IsDBNull(i) ? "\u0000NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                sha.AppendData(System.Text.Encoding.UTF8.GetBytes(v));
                sha.AppendData(new byte[] { 0x1F });
            }
            sha.AppendData(new byte[] { 0x1E });
        }
        return n + ":" + Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void Exec(DbConnection conn, DbTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string?[]> RowsRaw(DbConnection conn, string sql, DbTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string?[]>();
        while (r.Read())
        {
            var row = new string?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            list.Add(row);
        }
        return list;
    }

    private static List<string[]> Rows(DbConnection conn, string sql, DbTransaction? tx = null) =>
        RowsRaw(conn, sql, tx).Select(r => r.Select(x => x ?? "").ToArray()).ToList();

    private static List<string?[]> Rows(DbConnection conn, string sql, DbTransaction? tx, bool raw) => RowsRaw(conn, sql, tx);
}

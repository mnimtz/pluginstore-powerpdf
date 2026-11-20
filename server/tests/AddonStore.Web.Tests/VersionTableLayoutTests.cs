using AddonStore.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.0.2: PackageVersions is rebuilt with the large reports at the end, content and ids unchanged.</summary>
public class VersionTableLayoutTests
{
    // the layout of a production database before S2.0.2 (EnsureCreated plus later ALTER TABLE ... ADD COLUMN)
    private const string OldTable = @"CREATE TABLE ""PackageVersions"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_PackageVersions"" PRIMARY KEY AUTOINCREMENT,
    ""PackageId"" TEXT NOT NULL, ""Version"" TEXT NOT NULL, ""Status"" INTEGER NOT NULL, ""Changelog"" TEXT NOT NULL,
    ""ManifestJson"" TEXT NOT NULL, ""AtomNamespace"" TEXT NOT NULL, ""MinPowerPdfVersion"" TEXT NOT NULL, ""FilePath"" TEXT NOT NULL,
    ""Sha256"" TEXT NOT NULL, ""SizeBytes"" INTEGER NOT NULL, ""SubmittedAt"" TEXT NOT NULL, ""SubmittedBy"" TEXT NOT NULL,
    ""SubmittedVia"" TEXT NOT NULL, ""ValidationReportJson"" TEXT NOT NULL, ""ReviewedById"" TEXT NULL, ""ReviewedAt"" TEXT NULL,
    ""ReviewComment"" TEXT NULL, ""Downloads"" INTEGER NOT NULL, SourcePath TEXT NULL, SubmittedById TEXT NULL,
    SourceSizeBytes INTEGER NOT NULL DEFAULT 0, SourceSha256 TEXT NULL, BlockedAt TEXT NULL, BlockReason TEXT NULL, BlockedBy TEXT NULL,
    SourceUploadedAt TEXT NULL, RulesSnapshotJson TEXT NULL, ApprovalJson TEXT NULL, SourceUploadedBy TEXT NULL, SourceReportJson TEXT NULL,
    AiReviewJson TEXT NULL, AiReviewAt TEXT NULL, AiReviewModel TEXT NULL, AiReviewLang TEXT NULL, AiReviewTranslationsJson TEXT NULL,
    OriginChildId TEXT NULL, OriginUat TEXT NULL, OriginNote TEXT NULL, OriginUploaderEmail TEXT NULL, OriginUploader TEXT NULL,
    OriginReviewJson TEXT NULL, OriginAt TEXT NULL,
    CONSTRAINT ""FK_PackageVersions_Packages_PackageId"" FOREIGN KEY (""PackageId"") REFERENCES ""Packages"" (""Id"") ON DELETE CASCADE)";

    private static SqliteConnection NewDb(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "vtl-" + Guid.NewGuid().ToString("N") + ".db");
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        Exec(c, @"CREATE TABLE ""Packages"" (""Id"" TEXT NOT NULL CONSTRAINT ""PK_Packages"" PRIMARY KEY)");
        Exec(c, OldTable);
        Exec(c, @"CREATE UNIQUE INDEX ""IX_PackageVersions_PackageId_Version"" ON ""PackageVersions"" (""PackageId"", ""Version"")");
        Exec(c, "INSERT INTO Packages (Id) VALUES ('a'), ('b')");
        var big = new string('x', 120_000);
        for (var i = 1; i <= 40; i++)
            Exec(c, "INSERT INTO PackageVersions (PackageId, Version, Status, Changelog, ManifestJson, AtomNamespace, MinPowerPdfVersion, FilePath, Sha256, SizeBytes, " +
                    "SubmittedAt, SubmittedBy, SubmittedVia, ValidationReportJson, Downloads, SourceReportJson, AiReviewJson, BlockReason, OriginAt) VALUES " +
                    $"('{(i % 2 == 0 ? "a" : "b")}', '1.0.{i}', 2, 'cl{i}', '{new string('m', 18_000)}{i}', 'ns', '', 'f{i}', 's{i}', {i * 10}, " +
                    $"'2026-11-{(i % 28) + 1:00}', 'u', 'web', '{big}{i}', {i}, '{big}', {(i % 3 == 0 ? "NULL" : $"'{big}'")}, " +
                    $"{(i % 5 == 0 ? "'blocked'" : "NULL")}, '2026-11-20')");
        Exec(c, "DELETE FROM PackageVersions WHERE Id = 40");                 // the newest version was deleted: its id must not come back
        return c;
    }

    private static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static List<string> Cols(SqliteConnection c)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT name FROM pragma_table_info('PackageVersions') ORDER BY cid";
        using var r = cmd.ExecuteReader(); var l = new List<string>(); while (r.Read()) l.Add(r.GetString(0)); return l;
    }
    private static string Dump(SqliteConnection c)
    {
        var cols = Cols(c).OrderBy(x => x, StringComparer.Ordinal).ToList();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(",", cols.Select(x => $"\"{x}\""))} FROM PackageVersions ORDER BY Id";
        using var r = cmd.ExecuteReader(); var sb = new System.Text.StringBuilder();
        while (r.Read()) for (var i = 0; i < r.FieldCount; i++) sb.Append(cols[i]).Append('=').Append(r.IsDBNull(i) ? "<null>" : r.GetValue(i).ToString()!.GetHashCode()).Append(';');
        return sb.ToString();
    }

    [Fact]
    public void Rebuild_moves_the_reports_to_the_end_and_keeps_everything_else()
    {
        using var c = NewDb(out var path);
        try
        {
            var before = Dump(c);
            var seqBefore = Convert.ToInt64(Scalar(c, "SELECT seq FROM sqlite_sequence WHERE name = 'PackageVersions'"));
            VersionTableLayout.Ensure(c, NullLogger.Instance);

            var cols = Cols(c);
            Assert.Equal(VersionTableLayout.Desired(cols), cols);
            Assert.Equal("Id", cols[0]);
            var firstLarge = cols.IndexOf("ValidationReportJson");
            Assert.True(cols.IndexOf("Downloads") < cols.IndexOf("ManifestJson") && cols.IndexOf("ManifestJson") < firstLarge);
            Assert.True(cols.IndexOf("OriginAt") < firstLarge && cols.IndexOf("BlockReason") < firstLarge);
            Assert.Equal(before, Dump(c));                                                    // every value of every row
            Assert.Equal(seqBefore, Convert.ToInt64(Scalar(c, "SELECT seq FROM sqlite_sequence WHERE name = 'PackageVersions'")));
            Assert.Equal(1L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_PackageVersions_PackageId_Version'")));
            Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PackageVersions_layout'")));
            Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'PackageVersions_layout'")));

            // the next version gets a new id, the unique index and the foreign key still work
            Exec(c, "PRAGMA foreign_keys = ON");
            Exec(c, "INSERT INTO PackageVersions (PackageId, Version, Status, Changelog, ManifestJson, AtomNamespace, MinPowerPdfVersion, FilePath, Sha256, SizeBytes, SubmittedAt, SubmittedBy, SubmittedVia, ValidationReportJson, Downloads) " +
                    "VALUES ('a', '2.0.0', 0, '', '{}', '', '', '', '', 0, '2026-11-20', 'u', 'web', '{}', 0)");
            Assert.Equal(41L, Convert.ToInt64(Scalar(c, "SELECT MAX(Id) FROM PackageVersions")));
            Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO PackageVersions (PackageId, Version, Status, Changelog, ManifestJson, AtomNamespace, MinPowerPdfVersion, FilePath, Sha256, SizeBytes, SubmittedAt, SubmittedBy, SubmittedVia, ValidationReportJson, Downloads) " +
                    "VALUES ('a', '2.0.0', 0, '', '{}', '', '', '', '', 0, '2026-11-20', 'u', 'web', '{}', 0)"));       // unique (PackageId, Version)
            Assert.Throws<SqliteException>(() => Exec(c, "INSERT INTO PackageVersions (PackageId, Version, Status, Changelog, ManifestJson, AtomNamespace, MinPowerPdfVersion, FilePath, Sha256, SizeBytes, SubmittedAt, SubmittedBy, SubmittedVia, ValidationReportJson, Downloads) " +
                    "VALUES ('nope', '1.0.0', 0, '', '{}', '', '', '', '', 0, '2026-11-20', 'u', 'web', '{}', 0)"));      // foreign key
            Exec(c, "DELETE FROM Packages WHERE Id = 'b'");                                       // ON DELETE CASCADE kept
            Assert.Equal(0L, Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM PackageVersions WHERE PackageId = 'b'")));

            // idempotent: a second run changes nothing
            var again = Dump(c);
            VersionTableLayout.Ensure(c, NullLogger.Instance);
            Assert.Equal(again, Dump(c));

            // a column added later moves forward at the next start
            Exec(c, "ALTER TABLE PackageVersions ADD COLUMN LaterFlag INTEGER NOT NULL DEFAULT 7");
            VersionTableLayout.Ensure(c, NullLogger.Instance);
            var cols2 = Cols(c);
            Assert.True(cols2.IndexOf("LaterFlag") < cols2.IndexOf("Changelog"));
            Assert.Equal(7L, Convert.ToInt64(Scalar(c, "SELECT MIN(LaterFlag) FROM PackageVersions")));
        }
        finally { c.Close(); SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void Desired_order_keeps_small_columns_first()
    {
        var d = VersionTableLayout.Desired(new[] { "Id", "A", "ManifestJson", "ValidationReportJson", "B", "Changelog", "AiReviewJson", "C" });
        Assert.Equal(new[] { "Id", "A", "B", "C", "Changelog", "ManifestJson", "ValidationReportJson", "AiReviewJson" }, d);
    }
}

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using AddonStore.Web.Services;

namespace AddonStore.Web.Data;

/// <summary>
/// Brings any database (fresh, current or restored from an older backup) to
/// the current schema and data conventions. Runs at startup and right after a
/// restore; every step is idempotent.
/// </summary>
public static class SchemaUpgrade
{
    /// <summary>The roles in display order; "Developer" was called "User" before S0.9.0.</summary>
    public static readonly string[] Roles = { "Developer", "Reviewer", "Admin" };
    public const string DefaultRole = "Developer";

    public static async Task RunAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        // Poor-man migrations: EnsureCreated never alters an existing database, so
        // additions arrive as idempotent statements here.
        foreach (var sql in new[]
        {
            "ALTER TABLE AspNetUsers ADD COLUMN AvatarFile TEXT NULL",
            "ALTER TABLE AspNetUsers ADD COLUMN ShowContactPublicly INTEGER NOT NULL DEFAULT 1",
            "CREATE TABLE IF NOT EXISTS AppSettings (Key TEXT NOT NULL PRIMARY KEY, Value TEXT NOT NULL)",
            "CREATE TABLE IF NOT EXISTS Invites (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, " +
                "Email TEXT NOT NULL, TokenHash TEXT NOT NULL, Role TEXT NOT NULL, InvitedBy TEXT NOT NULL, " +
                "CreatedAt TEXT NOT NULL, AcceptedAt TEXT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Invites_TokenHash ON Invites (TokenHash)",
            "ALTER TABLE AspNetUsers ADD COLUMN NotifyAboutPlugins INTEGER NOT NULL DEFAULT 1",
            "ALTER TABLE Packages ADD COLUMN NameJson TEXT NULL",
            "ALTER TABLE Packages ADD COLUMN CategoryOverride TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN SourcePath TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN SourceSizeBytes INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE PackageVersions ADD COLUMN SourceSha256 TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN SourceUploadedAt TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN SourceUploadedBy TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN SourceReportJson TEXT NULL",
            "CREATE TABLE IF NOT EXISTS Categories (Slug TEXT NOT NULL PRIMARY KEY, NameJson TEXT NULL, " +
                "Builtin INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL)",
            "ALTER TABLE Packages ADD COLUMN DescriptionJson TEXT NULL",
            "ALTER TABLE Packages ADD COLUMN Author TEXT NULL",
            "ALTER TABLE Packages ADD COLUMN ContactEmail TEXT NULL",
            "ALTER TABLE Packages ADD COLUMN MetaUpdatedAt TEXT NULL",
            "ALTER TABLE Packages ADD COLUMN MetaUpdatedBy TEXT NULL",
            // S0.9.0: role "User" became "Developer". Renaming keeps the role id,
            // so every existing user-role link stays valid.
            "UPDATE AspNetRoles SET Name = 'Developer', NormalizedName = 'DEVELOPER' " +
                "WHERE Name = 'User' AND NOT EXISTS (SELECT 1 FROM AspNetRoles WHERE Name = 'Developer')",
            "UPDATE Invites SET Role = 'Developer' WHERE Role = 'User'",
            "CREATE TABLE IF NOT EXISTS UsageStats (Day TEXT NOT NULL, Kind TEXT NOT NULL, PackageId TEXT NOT NULL, " +
                "Version TEXT NOT NULL, Source TEXT NOT NULL, Lang TEXT NOT NULL, ClientVersion TEXT NOT NULL, " +
                "Country TEXT NOT NULL, Region TEXT NOT NULL, City TEXT NOT NULL, Org TEXT NOT NULL, " +
                "Arch TEXT NOT NULL, HostVersion TEXT NOT NULL, OsVersion TEXT NOT NULL, " +
                "Count INTEGER NOT NULL, PRIMARY KEY (Day, Kind, PackageId, Version, Source, Lang, ClientVersion, " +
                "Country, Region, City, Org, Arch, HostVersion, OsVersion))",
            "CREATE TABLE IF NOT EXISTS UsageEvents (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, " +
                "Kind TEXT NOT NULL, PackageId TEXT NOT NULL, Version TEXT NOT NULL, Source TEXT NOT NULL, Lang TEXT NOT NULL, " +
                "ClientVersion TEXT NOT NULL, HostVersion TEXT NOT NULL, OsVersion TEXT NOT NULL, Arch TEXT NOT NULL, " +
                "Country TEXT NOT NULL, Region TEXT NOT NULL, City TEXT NOT NULL, Latitude REAL NULL, Longitude REAL NULL, " +
                "Asn INTEGER NULL, Org TEXT NOT NULL, Ip TEXT NOT NULL, Hostname TEXT NULL, UserAgent TEXT NOT NULL)",
            "CREATE INDEX IF NOT EXISTS IX_UsageEvents_At ON UsageEvents (At)",
            "CREATE TABLE IF NOT EXISTS ShareStats (Day TEXT NOT NULL, PackageId TEXT NOT NULL, Ref TEXT NOT NULL, " +
                "Kind TEXT NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY (Day, PackageId, Ref, Kind))",
            "CREATE TABLE IF NOT EXISTS Ratings (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PackageId TEXT NOT NULL, " +
                "InstallHash TEXT NOT NULL, Stars INTEGER NOT NULL, Version TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Ratings_PackageId_InstallHash ON Ratings (PackageId, InstallHash)",
            "CREATE TABLE IF NOT EXISTS Feedback (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PackageId TEXT NOT NULL, " +
                "Version TEXT NOT NULL, Kind TEXT NOT NULL, Message TEXT NOT NULL, Email TEXT NULL, ClientInfo TEXT NOT NULL, " +
                "LogExcerpt TEXT NULL, Country TEXT NOT NULL, InstallHash TEXT NOT NULL, CreatedAt TEXT NOT NULL, " +
                "Status TEXT NOT NULL, DoneAt TEXT NULL, DoneBy TEXT NULL)",
            "CREATE INDEX IF NOT EXISTS IX_Feedback_PackageId_CreatedAt ON Feedback (PackageId, CreatedAt)",
            "ALTER TABLE Feedback ADD COLUMN AiCategory TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiSeverity TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiLanguage TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiSummaryEn TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiSummaryDe TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiReply TEXT NULL",
            "ALTER TABLE Feedback ADD COLUMN AiDuplicateOf INTEGER NULL",
            "ALTER TABLE Feedback ADD COLUMN AiAt TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN AiReviewJson TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN AiReviewAt TEXT NULL",
            "ALTER TABLE PackageVersions ADD COLUMN AiReviewModel TEXT NULL",
            // Customer deliveries (S0.14.0)
            "ALTER TABLE Packages ADD COLUMN Visibility TEXT NOT NULL DEFAULT 'public'",
            "CREATE TABLE IF NOT EXISTS Customers (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, " +
                "ContactName TEXT NULL, ContactEmail TEXT NULL, Language TEXT NOT NULL, Note TEXT NULL, Status TEXT NOT NULL, " +
                "OwnerId TEXT NOT NULL, CreatedAt TEXT NOT NULL, LastSeenAt TEXT NULL)",
            "CREATE TABLE IF NOT EXISTS CustomerCodes (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, CustomerId INTEGER NOT NULL, " +
                "DeliveryId INTEGER NULL, CodeHash TEXT NOT NULL, CodeProtected TEXT NOT NULL, Prefix TEXT NOT NULL, " +
                "CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, ExpiresAt TEXT NULL, RevokedAt TEXT NULL, LastUsedAt TEXT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomerCodes_CodeHash ON CustomerCodes (CodeHash)",
            "CREATE TABLE IF NOT EXISTS Deliveries (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, CustomerId INTEGER NOT NULL, " +
                "PackageId TEXT NOT NULL, BetaMode TEXT NOT NULL, BetaVersion TEXT NULL, LiveMode TEXT NOT NULL, LiveVersion TEXT NULL, " +
                "StartsAt TEXT NULL, EndsAt TEXT NULL, Status TEXT NOT NULL, CreatedBy TEXT NOT NULL, CreatedAt TEXT NOT NULL, " +
                "UpdatedAt TEXT NULL, LastSeenAt TEXT NULL)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Deliveries_CustomerId_PackageId ON Deliveries (CustomerId, PackageId)"
        })
        {
            try { db.Database.ExecuteSqlRaw(sql); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* column/table already there */ }
        }

        await CategoryService.SeedAsync(db, services.GetRequiredService<IStringLocalizer<SharedResource>>());

        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
    }
}

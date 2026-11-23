namespace AddonStore.Web.Data;

// "Git & Veracode" (S2.8.0): own tables, configured on the admin page /Admin/GitScan.
// Concept: docs/konzept-git-veracode.md

/// <summary>
/// An add-on taken out of service for now (S2.8.0): "paused" = no new installations or updates, installed copies keep
/// running; "disabled" = in addition, store clients from 2.3.0 switch installed copies off at the next Power PDF start
/// without removing them. The security block (S1.0.11) stays the strongest step and is not stored here.
/// </summary>
public class AddonSuspension
{
    public string PackageId { get; set; } = "";
    public string Level { get; set; } = "paused";           // paused, disabled
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public DateTime? ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    /// <summary>Date to look at it again; the store reminds the admins from then on, once a day.</summary>
    public DateTime? ReviewAt { get; set; }
    public DateTime? LastReminderAt { get; set; }
    /// <summary>Customers with a delivery of the add-on were told (their contact address).</summary>
    public bool CustomersNotified { get; set; }
}

/// <summary>An add-on (or the store itself) linked with a repository whose workflow builds and scans it.</summary>
public class GitScanLink
{
    public int Id { get; set; }
    /// <summary>Package id, or <see cref="Services.GitScan.GitScanService.ServerTarget"/> for the store server.</summary>
    public string Target { get; set; } = "";
    /// <summary>owner/name as the git server writes it.</summary>
    public string Repo { get; set; } = "";
    /// <summary>Sub-folder of a repository with several add-ons; empty = the whole repository.</summary>
    public string Path { get; set; } = "";
    /// <summary>Release tags, e.g. v* or measuretools-v*.</summary>
    public string TagPattern { get; set; } = "v*";
    public string Workflow { get; set; } = ".github/workflows/addon-release.yml";
    /// <summary>default (the global rule), off, beta, live.</summary>
    public string AutoRelease { get; set; } = "default";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Something that came from the git server or Veracode: a webhook delivery, a connection test.</summary>
public class GitScanEvent
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "";                 // webhook, test-git, test-veracode
    public string Event { get; set; } = "";                  // e.g. ping, workflow_run, release
    public string DeliveryId { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Result { get; set; } = "";                 // accepted, ignored, refused, ok, failed
    public string Detail { get; set; } = "";
    /// <summary>SHA-256 of a webhook body: a signed body replayed under another delivery id is recognised (review S2.8.0).</summary>
    public string BodyHash { get; set; } = "";
}

public static class GitScanSchema
{
    public static readonly string[] Statements =
    {
        "CREATE TABLE IF NOT EXISTS AddonSuspensions (PackageId TEXT NOT NULL PRIMARY KEY, Level TEXT NOT NULL, Reason TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, ChangedAt TEXT NULL, ChangedBy TEXT NULL, ReviewAt TEXT NULL, " +
            "LastReminderAt TEXT NULL, CustomersNotified INTEGER NOT NULL DEFAULT 0)",
        "CREATE TABLE IF NOT EXISTS GitScanLinks (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Target TEXT NOT NULL, Repo TEXT NOT NULL, " +
            "Path TEXT NOT NULL, TagPattern TEXT NOT NULL, Workflow TEXT NOT NULL, AutoRelease TEXT NOT NULL, CreatedAt TEXT NOT NULL, " +
            "CreatedBy TEXT NOT NULL, UpdatedAt TEXT NULL, UpdatedBy TEXT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_GitScanLinks_Target ON GitScanLinks (Target)",
        "CREATE TABLE IF NOT EXISTS GitScanEvents (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, Source TEXT NOT NULL, " +
            "Event TEXT NOT NULL, DeliveryId TEXT NOT NULL, Repo TEXT NOT NULL, Result TEXT NOT NULL, Detail TEXT NOT NULL, BodyHash TEXT NOT NULL DEFAULT '')",
        "ALTER TABLE GitScanEvents ADD COLUMN BodyHash TEXT NOT NULL DEFAULT ''",
        "CREATE INDEX IF NOT EXISTS IX_GitScanEvents_At ON GitScanEvents (At)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_GitScanEvents_Delivery ON GitScanEvents (DeliveryId) WHERE Source = 'webhook'",
        // review S2.8.0: unique, so two replays of one signed body at the same moment cannot both pass
        "DROP INDEX IF EXISTS IX_GitScanEvents_BodyHash",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_GitScanEvents_BodyHashU ON GitScanEvents (BodyHash) WHERE Source = 'webhook' AND BodyHash <> ''",
    };
}

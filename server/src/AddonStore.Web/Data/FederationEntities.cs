namespace AddonStore.Web.Data;

// UAT environments (docs/concepts/master-child.md, S1.21.0): a production server becomes the
// master of a UAT only when an admin adds one. These two tables live on the master; the UAT
// keeps its side (mode, master address, keys) in AppSettings. All tables are prefixed "Federation".

/// <summary>A paired UAT (child) of this server: its public key verifies every request it sends.</summary>
public class FederationChild
{
    /// <summary>Pairing id, 16 hex characters, sent by the child with every request.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>ECDSA P-256 public key of the child, SubjectPublicKeyInfo in base64.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Portal address the child reported when it paired (information only).</summary>
    public string ChildUrl { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    /// <summary>Master admin who created the pairing code (S1.22.0): uploader of handed-over versions when no account has the UAT uploader's e-mail.</summary>
    public string? CreatedById { get; set; }
    public DateTime? BlockedAt { get; set; }
    public string? BlockedBy { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>A one-time pairing code an admin created with "Add UAT"; only its hash is stored.</summary>
public class FederationPairingCode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Name the UAT gets once it pairs with this code.</summary>
    public string Name { get; set; } = "";
    /// <summary>SHA-256 (hex) of the normalized code.</summary>
    public string CodeHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public string? CreatedById { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? ChildId { get; set; }
}

/// <summary>
/// Master side of a handover (S1.22.0, stage E2): a UAT announces one version (id, version, SHA-256 of package and
/// source, note, its uploader and review), then sends the upload package; the master checks it like any upload.
/// </summary>
public class FederationIntake
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChildId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string Note { get; set; } = "";
    public string UploaderEmail { get; set; } = "";
    public string UploaderName { get; set; } = "";
    public string HandedOverBy { get; set; } = "";
    public string ReviewJson { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>waiting (announced), accepted (stored as submitted version), rejected (findings).</summary>
    public string State { get; set; } = "waiting";
    public string Message { get; set; } = "";
    public string FindingsJson { get; set; } = "";
    public int? VersionId { get; set; }
    public DateTime? DoneAt { get; set; }
    /// <summary>S1.23.0 (E3): the UAT's catalog entry as a proposal {name, description, author, contactEmail, category, providers}.</summary>
    public string CatalogJson { get; set; } = "";
    public DateTime? CatalogAppliedAt { get; set; }
    public string? CatalogAppliedBy { get; set; }
}

/// <summary>UAT side of a handover (S1.22.0): what was handed to production and the answer.</summary>
public class FederationHandover
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int VersionId { get; set; }
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    /// <summary>sending, accepted (submitted on production), rejected (findings), failed (not delivered).</summary>
    public string State { get; set; } = "sending";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string FindingsJson { get; set; } = "";
    public string MasterIntakeId { get; set; } = "";
    public DateTime? DoneAt { get; set; }
    /// <summary>S1.23.0 (E3): state of the version on production: submitted, beta, live, rejected, withdrawn, blocked.</summary>
    public string MasterStatus { get; set; } = "";
    public DateTime? MasterStatusAt { get; set; }
    public string MasterComment { get; set; } = "";
}

/// <summary>Idempotent SQL for databases created before S1.21.0 (EnsureCreated never alters).</summary>
public static class FederationSchema
{
    public static readonly string[] Statements =
    {
        "CREATE TABLE IF NOT EXISTS FederationChildren (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, PublicKey TEXT NOT NULL, " +
            "ChildUrl TEXT NOT NULL, CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, BlockedAt TEXT NULL, BlockedBy TEXT NULL, LastSeenAt TEXT NULL)",
        "CREATE TABLE IF NOT EXISTS FederationPairingCodes (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CodeHash TEXT NOT NULL, " +
            "CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, ExpiresAt TEXT NOT NULL, UsedAt TEXT NULL, ChildId TEXT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_FederationPairingCodes_CodeHash ON FederationPairingCodes (CodeHash)",
        // S1.22.0 (stage E2): handing versions to production
        "ALTER TABLE FederationChildren ADD COLUMN CreatedById TEXT NULL",
        "ALTER TABLE FederationPairingCodes ADD COLUMN CreatedById TEXT NULL",
        "CREATE TABLE IF NOT EXISTS FederationIntakes (Id TEXT NOT NULL PRIMARY KEY, ChildId TEXT NOT NULL, PackageId TEXT NOT NULL, Version TEXT NOT NULL, " +
            "Sha256 TEXT NOT NULL, SourceSha256 TEXT NOT NULL, Note TEXT NOT NULL, UploaderEmail TEXT NOT NULL, UploaderName TEXT NOT NULL, " +
            "HandedOverBy TEXT NOT NULL, ReviewJson TEXT NOT NULL, CreatedAt TEXT NOT NULL, State TEXT NOT NULL, Message TEXT NOT NULL, " +
            "FindingsJson TEXT NOT NULL, VersionId INTEGER NULL, DoneAt TEXT NULL)",
        "CREATE TABLE IF NOT EXISTS FederationHandovers (Id TEXT NOT NULL PRIMARY KEY, VersionId INTEGER NOT NULL, PackageId TEXT NOT NULL, " +
            "Version TEXT NOT NULL, Sha256 TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, State TEXT NOT NULL, " +
            "Code TEXT NOT NULL, Message TEXT NOT NULL, FindingsJson TEXT NOT NULL, MasterIntakeId TEXT NOT NULL, DoneAt TEXT NULL)",
        "ALTER TABLE PackageVersions ADD COLUMN OriginChildId TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginUat TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginNote TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginUploaderEmail TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginUploader TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginReviewJson TEXT NULL",
        "ALTER TABLE PackageVersions ADD COLUMN OriginAt TEXT NULL",
        // S1.23.0 (stage E3): status back to the UAT, catalog proposal
        "ALTER TABLE FederationIntakes ADD COLUMN CatalogJson TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE FederationIntakes ADD COLUMN CatalogAppliedAt TEXT NULL",
        "ALTER TABLE FederationIntakes ADD COLUMN CatalogAppliedBy TEXT NULL",
        "ALTER TABLE FederationHandovers ADD COLUMN MasterStatus TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE FederationHandovers ADD COLUMN MasterStatusAt TEXT NULL",
        "ALTER TABLE FederationHandovers ADD COLUMN MasterComment TEXT NOT NULL DEFAULT ''",
        // S1.24.0 (stages E4/E5): templates from a UAT
        "ALTER TABLE DeliveryTemplates ADD COLUMN OriginChildId TEXT NULL",
        "ALTER TABLE DeliveryTemplates ADD COLUMN OriginTemplateId INTEGER NULL",
        "ALTER TABLE DeliveryTemplates ADD COLUMN HandedOverAt TEXT NULL",
    };
}

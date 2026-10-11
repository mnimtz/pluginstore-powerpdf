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
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? ChildId { get; set; }
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
    };
}

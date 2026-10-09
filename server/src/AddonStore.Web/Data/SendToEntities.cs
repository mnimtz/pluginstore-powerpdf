namespace AddonStore.Web.Data;

// "Senden an" (docs/concepts/senden-an.md): documents between confirmed
// contacts, end-to-end encrypted on the PCs. The server keeps who is
// connected with whom and the encrypted envelopes; the document blocks live
// in the document storage (Blob), never in this database.
// All tables are prefixed "SendTo" (the store already has an Invites table).

/// <summary>A person using "Senden an" (one per e-mail of the Power PDF sign-in).</summary>
public class SendToUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>identity, email, windows or user (edited by the person).</summary>
    public string NameSource { get; set; } = "email";
    /// <summary>Name the add-on derived, for "Reset".</summary>
    public string DerivedName { get; set; } = "";
    /// <summary>Store customer of the device's customer code, if any (customer-owned storage).</summary>
    public int? CustomerId { get; set; }
    public DateTime? BlockedAt { get; set; }
    public string? BlockedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One Power PDF installation of a user (per Windows account).</summary>
public class SendToDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    /// <summary>X25519 public key, base64 (32 bytes).</summary>
    public string KemPub { get; set; } = "";
    /// <summary>ECDSA P-256 public key, base64 (65 bytes, uncompressed).</summary>
    public string SigPub { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

public class SendToInvitation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FromUserId { get; set; } = "";
    public string ToEmail { get; set; } = "";
    public string? ToUserId { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>open, accepted, declined, expired, withdrawn.</summary>
    public string Status { get; set; } = "open";
    /// <summary>Language of the invitation mail and page (the inviter's).</summary>
    public string Lang { get; set; } = "en";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

/// <summary>A confirmed contact; one row per pair (UserA &lt; UserB).</summary>
public class SendToContact
{
    public int Id { get; set; }
    public string UserA { get; set; } = "";
    public string UserB { get; set; } = "";
    public DateTime Since { get; set; } = DateTime.UtcNow;
}

public class SendToBlock
{
    public int Id { get; set; }
    public string BlockerId { get; set; } = "";
    public string BlockedId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>"Report" from the add-on; shown to store admins under Senden an → Reports.</summary>
public class SendToReport
{
    public int Id { get; set; }
    public string ReporterId { get; set; } = "";
    public string ReportedId { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>open, done.</summary>
    public string Status { get; set; } = "open";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DoneAt { get; set; }
    public string? DoneBy { get; set; }
}

public class SendToList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Favorite { get; set; }
    /// <summary>JSON array of user ids (confirmed contacts only).</summary>
    public string MembersJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SendToQuick
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    /// <summary>user or list.</summary>
    public string TargetType { get; set; } = "user";
    public string TargetId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Note { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public bool InRibbon { get; set; } = true;
    public int SortOrder { get; set; }
}

public class SendToTransfer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SenderUserId { get; set; } = "";
    public string SenderDeviceId { get; set; } = "";
    public long Size { get; set; }
    public int ChunkCount { get; set; }
    /// <summary>Uploaded chunk indexes, comma separated.</summary>
    public string Uploaded { get; set; } = "";
    /// <summary>"tungsten" or "customer:{id}": where the blocks are stored.</summary>
    public string Storage { get; set; } = "tungsten";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>End of the undo window; not delivered before.</summary>
    public DateTime DeliverAfter { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Cancelled { get; set; }
    /// <summary>Blocks deleted (all envelopes resolved, cancelled or expired).</summary>
    public DateTime? PurgedAt { get; set; }
}

public class SendToEnvelope
{
    public int Id { get; set; }
    public string TransferId { get; set; } = "";
    public string RecipientUserId { get; set; } = "";
    public string RecipientDeviceId { get; set; } = "";
    /// <summary>HPKE enc || ciphertext, base64 (file name, note and file key inside; unreadable here).</summary>
    public string Data { get; set; } = "";
    public string Signature { get; set; } = "";
    /// <summary>waiting, accepted, declined, expired, removed, cancelled.</summary>
    public string Status { get; set; } = "waiting";
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>Idempotent SQL for databases created before "Senden an" (EnsureCreated never alters).</summary>
public static class SendToSchema
{
    public static readonly string[] Statements =
    {
        "CREATE TABLE IF NOT EXISTS SendToUsers (Id TEXT NOT NULL PRIMARY KEY, Email TEXT NOT NULL, Domain TEXT NOT NULL, Name TEXT NOT NULL, " +
            "NameSource TEXT NOT NULL, DerivedName TEXT NOT NULL, CustomerId INTEGER NULL, BlockedAt TEXT NULL, BlockedBy TEXT NULL, CreatedAt TEXT NOT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_SendToUsers_Email ON SendToUsers (Email)",
        "CREATE TABLE IF NOT EXISTS SendToDevices (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, KemPub TEXT NOT NULL, SigPub TEXT NOT NULL, " +
            "TokenHash TEXT NOT NULL, CreatedAt TEXT NOT NULL, LastSeenAt TEXT NOT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_SendToDevices_TokenHash ON SendToDevices (TokenHash)",
        "CREATE INDEX IF NOT EXISTS IX_SendToDevices_UserId ON SendToDevices (UserId)",
        "CREATE TABLE IF NOT EXISTS SendToInvitations (Id TEXT NOT NULL PRIMARY KEY, FromUserId TEXT NOT NULL, ToEmail TEXT NOT NULL, ToUserId TEXT NULL, " +
            "TokenHash TEXT NOT NULL, Status TEXT NOT NULL, Lang TEXT NOT NULL, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, AnsweredAt TEXT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_SendToInvitations_TokenHash ON SendToInvitations (TokenHash)",
        "CREATE INDEX IF NOT EXISTS IX_SendToInvitations_ToEmail ON SendToInvitations (ToEmail)",
        "CREATE TABLE IF NOT EXISTS SendToContacts (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, UserA TEXT NOT NULL, UserB TEXT NOT NULL, Since TEXT NOT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_SendToContacts_UserA_UserB ON SendToContacts (UserA, UserB)",
        "CREATE TABLE IF NOT EXISTS SendToBlocks (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, BlockerId TEXT NOT NULL, BlockedId TEXT NOT NULL, CreatedAt TEXT NOT NULL)",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_SendToBlocks_BlockerId_BlockedId ON SendToBlocks (BlockerId, BlockedId)",
        "CREATE TABLE IF NOT EXISTS SendToReports (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, ReporterId TEXT NOT NULL, ReportedId TEXT NOT NULL, " +
            "Reason TEXT NOT NULL, Status TEXT NOT NULL, CreatedAt TEXT NOT NULL, DoneAt TEXT NULL, DoneBy TEXT NULL)",
        "CREATE TABLE IF NOT EXISTS SendToLists (Id TEXT NOT NULL PRIMARY KEY, OwnerId TEXT NOT NULL, Name TEXT NOT NULL, Favorite INTEGER NOT NULL, " +
            "MembersJson TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE IF NOT EXISTS SendToQuicks (Id TEXT NOT NULL PRIMARY KEY, OwnerId TEXT NOT NULL, TargetType TEXT NOT NULL, TargetId TEXT NOT NULL, " +
            "Label TEXT NOT NULL, Note TEXT NOT NULL, Shortcut TEXT NOT NULL, InRibbon INTEGER NOT NULL, SortOrder INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS SendToTransfers (Id TEXT NOT NULL PRIMARY KEY, SenderUserId TEXT NOT NULL, SenderDeviceId TEXT NOT NULL, Size INTEGER NOT NULL, " +
            "ChunkCount INTEGER NOT NULL, Uploaded TEXT NOT NULL, Storage TEXT NOT NULL, CreatedAt TEXT NOT NULL, DeliverAfter TEXT NOT NULL, ExpiresAt TEXT NOT NULL, " +
            "Cancelled INTEGER NOT NULL, PurgedAt TEXT NULL)",
        "CREATE TABLE IF NOT EXISTS SendToEnvelopes (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, TransferId TEXT NOT NULL, RecipientUserId TEXT NOT NULL, " +
            "RecipientDeviceId TEXT NOT NULL, Data TEXT NOT NULL, Signature TEXT NOT NULL, Status TEXT NOT NULL, ResolvedAt TEXT NULL)",
        "CREATE INDEX IF NOT EXISTS IX_SendToEnvelopes_RecipientDeviceId ON SendToEnvelopes (RecipientDeviceId)",
        "CREATE INDEX IF NOT EXISTS IX_SendToEnvelopes_TransferId ON SendToEnvelopes (TransferId)",
    };
}

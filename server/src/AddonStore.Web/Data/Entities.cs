using Microsoft.AspNetCore.Identity;

namespace AddonStore.Web.Data;

public enum UserStatus
{
    Pending = 0,
    Active = 1,
    Rejected = 2,
    Disabled = 3
}

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public UserStatus Status { get; set; } = UserStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>File name of the avatar under data/avatars, e.g. "<id>.png"; null = none.</summary>
    public string? AvatarFile { get; set; }
    /// <summary>Show name + email as author on the public catalog; off = "Tungsten Automation".</summary>
    public bool ShowContactPublicly { get; set; } = true;
    /// <summary>Emails about the user's own plug-ins (upload receipt, review result, status changes).</summary>
    public bool NotifyAboutPlugins { get; set; } = true;
}

/// <summary>
/// Catalog category. The store starts with eight built-in ones; uploads may
/// propose a new high-level category (see CategoryService), admins merge or
/// delete them.
/// </summary>
public class Category
{
    public string Slug { get; set; } = "";
    /// <summary>{"en": "...", "de": "...", ...} in all 16 languages.</summary>
    public string? NameJson { get; set; }
    public bool Builtin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
}

/// <summary>Runtime-editable instance settings (admin area), e.g. the Resend key.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Daily usage counter for the admin reports (S0.9.0). One row per day and
/// combination; the IP address itself is not stored here, only what was
/// derived from it (country, region, city, network operator).
/// Kind: "download" (package file), "msi" (client installer from the
/// website), "catalog" (catalog fetch = store window opened).
/// Source: "client" (Add-on Store client), "web" (browser), "api" (other).
/// </summary>
public class UsageStat
{
    public string Day { get; set; } = "";            // yyyy-MM-dd (UTC)
    public string Kind { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string Lang { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string Country { get; set; } = "";        // ISO 3166 alpha-2 from the IP, IP not stored
    public string Region { get; set; } = "";         // state / region from the IP
    public string City { get; set; } = "";           // city from the IP
    public string Org { get; set; } = "";            // network operator (ASN organisation) from the IP
    public string Arch { get; set; } = "";           // client: x64 / arm64
    public string HostVersion { get; set; } = "";    // client: Power PDF version
    public string OsVersion { get; set; } = "";      // client: Windows version (major.minor.build)
    public int Count { get; set; }
}

/// <summary>
/// Single request with IP address (S0.9.0). Written ONLY while an admin has
/// switched on IP logging with the GDPR confirmation (setting Usage.IpLogging);
/// deleted after Usage.IpRetentionDays by UsageMaintenance.
/// </summary>
public class UsageEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Source { get; set; } = "";
    public string Lang { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string HostVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Country { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public long? Asn { get; set; }
    public string Org { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>Reverse DNS name, resolved in the background; null = not tried yet, "" = none.</summary>
    public string? Hostname { get; set; }
    public string UserAgent { get; set; } = "";
}

/// <summary>
/// Star rating from an Add-on Store client (S0.11.0): one per installation and
/// package (InstallHash = SHA-256 of the client's random install id + package
/// id, so ratings of different packages cannot be linked). No personal data.
/// </summary>
public class Rating
{
    public int Id { get; set; }
    public string PackageId { get; set; } = "";
    public string InstallHash { get; set; } = "";
    public int Stars { get; set; }
    public string Version { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Problem report or comment sent from the Add-on Store client (S0.11.0); visible
/// to the package owner and admins (portal and API), never public.
/// </summary>
public class Feedback
{
    public int Id { get; set; }
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "problem";       // problem, comment
    public string Message { get; set; } = "";
    public string? Email { get; set; }                   // optional, given by the user for a reply
    public string ClientInfo { get; set; } = "";         // client user agent (versions, architecture)
    public string? LogExcerpt { get; set; }              // only when the user ticked "attach log"
    public string Country { get; set; } = "";
    public string InstallHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "open";         // open, done
    public DateTime? DoneAt { get; set; }
    public string? DoneBy { get; set; }

    // AI triage (S0.12.0, optional): filled by AiWorker when the admin enabled it.
    public string? AiCategory { get; set; }              // bug, wish, question, praise, other
    public string? AiSeverity { get; set; }              // low, medium, high
    public string? AiLanguage { get; set; }              // language of the report (ISO 639-1)
    public string? AiSummaryEn { get; set; }
    public string? AiSummaryDe { get; set; }
    public string? AiReply { get; set; }                 // suggested reply in the reporter's language
    public int? AiDuplicateOf { get; set; }
    public DateTime? AiAt { get; set; }
}

/// <summary>Daily counter for shared add-on links /a/{slug}?ref= (S0.10.0); Kind: view, install, client.</summary>
public class ShareStat
{
    public string Day { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>An email invitation; only the token hash is stored.</summary>
public class Invite
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Role { get; set; } = "Developer";
    public string InvitedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
}

/// <summary>Personal API token; only the SHA-256 hash is stored.</summary>
public class ApiToken
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public AppUser? User { get; set; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class Package
{
    /// <summary>Reverse-DNS id from the manifest, e.g. com.tungsten.smartbookmarks.</summary>
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public AppUser? Owner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PackageVersion> Versions { get; set; } = new();

    // Catalog entry edited on the server; null = take the value from the newest manifest.
    public string? NameJson { get; set; }          // {"en": "...", "de": "..."}
    public string? DescriptionJson { get; set; }   // all 16 languages
    public string? Author { get; set; }
    public string? ContactEmail { get; set; }
    /// <summary>Category set on the server (catalog entry or category merge); wins over the manifest.</summary>
    public string? CategoryOverride { get; set; }
    public DateTime? MetaUpdatedAt { get; set; }
    public string? MetaUpdatedBy { get; set; }
}

public enum VersionStatus
{
    Submitted = 0,
    Beta = 1,      // passed all hard automatic checks, visible to beta clients
    Live = 2,      // approved by an admin
    Rejected = 3,
    Withdrawn = 4
}

public class PackageVersion
{
    public int Id { get; set; }
    public string PackageId { get; set; } = "";
    public Package? Package { get; set; }
    public string Version { get; set; } = "";
    public VersionStatus Status { get; set; } = VersionStatus.Submitted;
    public string Changelog { get; set; } = "";
    public string ManifestJson { get; set; } = "{}";
    public string AtomNamespace { get; set; } = "";
    public string MinPowerPdfVersion { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>Source code ZIP next to the package (admins only), relative to the package storage.</summary>
    public string? SourcePath { get; set; }
    public long SourceSizeBytes { get; set; }
    public string? SourceSha256 { get; set; }
    public DateTime? SourceUploadedAt { get; set; }
    public string? SourceUploadedBy { get; set; }
    public string? SourceReportJson { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public string SubmittedBy { get; set; } = "";
    public string SubmittedVia { get; set; } = "";   // "web" or "api:<token name>"
    public string ValidationReportJson { get; set; } = "{}";
    public string? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public int Downloads { get; set; }
    // AI review aid (S0.12.0, optional): JSON result, when and with which model.
    public string? AiReviewJson { get; set; }
    public DateTime? AiReviewAt { get; set; }
    public string? AiReviewModel { get; set; }
}

public class AuditEntry
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Details { get; set; } = "";
}

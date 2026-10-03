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
}

/// <summary>Runtime-editable instance settings (admin area), e.g. the Resend key.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>An email invitation; only the token hash is stored.</summary>
public class Invite
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Role { get; set; } = "User";
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
    /// <summary>Reverse-DNS id from the manifest, e.g. com.tungsten.officekonverter.</summary>
    public string Id { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public AppUser? Owner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PackageVersion> Versions { get; set; } = new();
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
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public string SubmittedBy { get; set; } = "";
    public string SubmittedVia { get; set; } = "";   // "web" or "api:<token name>"
    public string ValidationReportJson { get; set; } = "{}";
    public string? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public int Downloads { get; set; }
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

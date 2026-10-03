using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<UsageStat> UsageStats => Set<UsageStat>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<ShareStat> ShareStats => Set<ShareStat>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<AppSetting>().HasKey(s => s.Key);
        b.Entity<Category>().HasKey(c => c.Slug);
        b.Entity<UsageStat>().HasKey(s => new { s.Day, s.Kind, s.PackageId, s.Version, s.Source, s.Lang, s.ClientVersion,
                                                s.Country, s.Region, s.City, s.Org, s.Arch, s.HostVersion, s.OsVersion });
        b.Entity<UsageEvent>().HasIndex(e => e.At);
        b.Entity<ShareStat>().HasKey(s => new { s.Day, s.PackageId, s.Ref, s.Kind });
        b.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<PackageVersion>().HasIndex(v => new { v.PackageId, v.Version }).IsUnique();
        b.Entity<AuditEntry>().HasIndex(a => a.At);
    }
}

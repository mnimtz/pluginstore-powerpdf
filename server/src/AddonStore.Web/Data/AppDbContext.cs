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

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<PackageVersion>().HasIndex(v => new { v.PackageId, v.Version }).IsUnique();
        b.Entity<AuditEntry>().HasIndex(a => a.At);
    }
}

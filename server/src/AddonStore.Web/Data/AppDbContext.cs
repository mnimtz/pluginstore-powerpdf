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
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<FeedbackNote> FeedbackNotes => Set<FeedbackNote>();
    public DbSet<FeedbackAttachment> FeedbackAttachments => Set<FeedbackAttachment>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerCode> CustomerCodes => Set<CustomerCode>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliverySeat> DeliverySeats => Set<DeliverySeat>();
    public DbSet<TestCode> TestCodes => Set<TestCode>();

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
        b.Entity<Rating>().HasIndex(r => new { r.PackageId, r.InstallHash }).IsUnique();
        b.Entity<Feedback>().ToTable("Feedback").HasIndex(f => new { f.PackageId, f.CreatedAt });
        b.Entity<Feedback>().HasIndex(f => f.Status);
        b.Entity<FeedbackNote>().HasIndex(n => n.FeedbackId);
        b.Entity<FeedbackAttachment>().HasIndex(a => a.FeedbackId);
        b.Entity<DeliverySeat>().HasIndex(s => new { s.DeliveryId, s.InstallHash });
        b.Entity<Invite>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<PackageVersion>().HasIndex(v => new { v.PackageId, v.Version }).IsUnique();
        b.Entity<AuditEntry>().HasIndex(a => a.At);
        b.Entity<CustomerCode>().HasIndex(c => c.CodeHash).IsUnique();
        b.Entity<TestCode>().HasIndex(c => c.CodeHash).IsUnique();
        b.Entity<TestCode>().HasIndex(c => c.UserId);
        b.Entity<Delivery>().HasIndex(d => new { d.CustomerId, d.PackageId }).IsUnique();
    }
}

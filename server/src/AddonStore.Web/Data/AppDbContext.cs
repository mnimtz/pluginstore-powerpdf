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
    public DbSet<PowerPdfLine> PowerPdfLines => Set<PowerPdfLine>();
    public DbSet<ClientInstall> ClientInstalls => Set<ClientInstall>();
    public DbSet<CustomerDomain> CustomerDomains => Set<CustomerDomain>();
    public DbSet<DeliveryTemplate> DeliveryTemplates => Set<DeliveryTemplate>();
    public DbSet<DeliveryTemplateItem> DeliveryTemplateItems => Set<DeliveryTemplateItem>();
    public DbSet<CustomerTemplate> CustomerTemplates => Set<CustomerTemplate>();
    // "Senden an" (Data/SendToEntities.cs)
    public DbSet<SendToUser> SendToUsers => Set<SendToUser>();
    public DbSet<SendToDevice> SendToDevices => Set<SendToDevice>();
    public DbSet<SendToInvitation> SendToInvitations => Set<SendToInvitation>();
    public DbSet<SendToContact> SendToContacts => Set<SendToContact>();
    public DbSet<SendToBlock> SendToBlocks => Set<SendToBlock>();
    public DbSet<SendToReport> SendToReports => Set<SendToReport>();
    public DbSet<SendToList> SendToLists => Set<SendToList>();
    public DbSet<SendToQuick> SendToQuicks => Set<SendToQuick>();
    public DbSet<SendToTransfer> SendToTransfers => Set<SendToTransfer>();
    public DbSet<SendToEnvelope> SendToEnvelopes => Set<SendToEnvelope>();

    // S1.17.3: inside an explicit transaction SaveChanges returns before the commit, so a catalog loaded in between
    // still read the old rows under the new generation; the commit of such a transaction starts one more generation
    private bool _catalogTouchedInTransaction;
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(CatalogCommitInterceptor.Instance);

    private sealed class CatalogCommitInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public static readonly CatalogCommitInterceptor Instance = new();
        public override void TransactionCommitted(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData) => Done(eventData.Context);
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Done(eventData.Context);
            return Task.CompletedTask;
        }
        private static void Done(DbContext? c)
        {
            if (c is AppDbContext db && db._catalogTouchedInTransaction) { db._catalogTouchedInTransaction = false; Services.CatalogUi.Invalidate(); }
        }
    }

    // S1.13.2: a write to what the public catalog shows starts a new catalog generation (CatalogUi cache)
    private bool TouchesCatalog() => ChangeTracker.Entries().Any(e =>
        e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
        e.Entity is Package or PackageVersion or Category or Rating or AppUser);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var touch = TouchesCatalog();
        if (touch && Database.CurrentTransaction is not null) _catalogTouchedInTransaction = true;
        var n = base.SaveChanges(acceptAllChangesOnSuccess);
        if (touch) Services.CatalogUi.Invalidate();
        return n;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var touch = TouchesCatalog();
        if (touch && Database.CurrentTransaction is not null) _catalogTouchedInTransaction = true;
        var n = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (touch) Services.CatalogUi.Invalidate();
        return n;
    }

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
        b.Entity<PowerPdfLine>().HasIndex(l => l.Key).IsUnique();
        b.Entity<ClientInstall>().HasIndex(c => c.InstallHash).IsUnique();
        b.Entity<ClientInstall>().HasIndex(c => c.Domain);
        b.Entity<CustomerDomain>().HasIndex(d => d.Domain).IsUnique();
        b.Entity<DeliveryTemplateItem>().HasIndex(i => new { i.TemplateId, i.PackageId }).IsUnique();
        b.Entity<CustomerTemplate>().HasIndex(a => new { a.CustomerId, a.TemplateId }).IsUnique();
        b.Entity<Delivery>().HasIndex(d => new { d.CustomerId, d.PackageId }).IsUnique();
        b.Entity<SendToUser>().HasIndex(u => u.Email).IsUnique();
        b.Entity<SendToDevice>().HasIndex(d => d.TokenHash).IsUnique();
        b.Entity<SendToDevice>().HasIndex(d => d.UserId);
        b.Entity<SendToInvitation>().HasIndex(i => i.TokenHash).IsUnique();
        b.Entity<SendToInvitation>().HasIndex(i => i.ToEmail);
        b.Entity<SendToContact>().HasIndex(c => new { c.UserA, c.UserB }).IsUnique();
        b.Entity<SendToBlock>().HasIndex(x => new { x.BlockerId, x.BlockedId }).IsUnique();
        b.Entity<SendToEnvelope>().HasIndex(e => e.RecipientDeviceId);
        b.Entity<SendToEnvelope>().HasIndex(e => e.TransferId);
    }
}

using AddonStore.Web.Data;

namespace AddonStore.Web.Services;

public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public async Task LogAsync(string actor, string action, string subject, string details = "")
    {
        _db.AuditEntries.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            Actor = actor,
            Action = action,
            Subject = subject,
            Details = details
        });
        await _db.SaveChangesAsync();
    }
}

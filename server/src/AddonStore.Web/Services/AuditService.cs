using AddonStore.Web.Data;

namespace AddonStore.Web.Services;

public class AuditService
{
    private readonly AppDbContext _db;

    public AuditService(AppDbContext db) => _db = db;

    public async Task LogAsync(string actor, string action, string subject, string details = "")
    {
        var entry = new AuditEntry
        {
            At = DateTime.UtcNow,
            Actor = actor,
            Action = action,
            Subject = subject,
            Details = details
        };
        _db.AuditEntries.Add(entry);
        // a database briefly locked by parallel writers (SQLite): try again shortly instead of failing the action (S1.13.1)
        for (var attempt = 1; ; attempt++)
        {
            try { await _db.SaveChangesAsync(); return; }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (attempt < 4 &&
                ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
            {
                await Task.Delay(100 * attempt * attempt);
            }
        }
    }
}

using AddonStore.Web.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages.Admin;

public class AuditModel : PageModel
{
    private readonly AppDbContext _db;

    public List<AuditEntry> Entries { get; private set; } = new();
    public string? Query { get; private set; }

    public AuditModel(AppDbContext db) => _db = db;

    public async Task OnGetAsync(string? q)
    {
        Query = q;
        var query = _db.AuditEntries.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => e.Actor.Contains(q) || e.Action.Contains(q) ||
                                     e.Subject.Contains(q) || e.Details.Contains(q));
        Entries = await query.OrderByDescending(e => e.At).Take(300).ToListAsync();
    }
}

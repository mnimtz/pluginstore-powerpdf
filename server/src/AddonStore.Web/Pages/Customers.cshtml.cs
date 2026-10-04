using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Pages;

/// <summary>Register "Customers" (S0.14.0): own customers (admins and reviewers: all) and creating new ones.</summary>
public class CustomersModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly CustomerService _customers;

    public record Row(Customer Customer, int Deliveries, string Owner);
    public List<Row> Rows { get; private set; } = new();
    public bool CanCreate { get; private set; }
    public string? Notice { get; private set; }
    public string NoticeKind { get; private set; } = "ok";
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public CustomersModel(AppDbContext db, UserManager<AppUser> users, CustomerService customers)
    {
        _db = db; _users = users; _customers = customers;
    }

    private async Task LoadAsync()
    {
        var me = await _users.GetUserAsync(User);
        CanCreate = me is not null && !(User.IsInRole("Reviewer") && !User.IsInRole("Admin"));
        var list = await _customers.Visible(User, me!.Id).OrderBy(c => c.Name).ToListAsync();
        if (!string.IsNullOrWhiteSpace(Q))
            list = list.Where(c => c.Name.Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                   (c.ContactName ?? "").Contains(Q.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var counts = await _db.Deliveries.GroupBy(d => d.CustomerId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var owners = await _db.Users.ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        Rows = list.Select(c => new Row(c, counts.GetValueOrDefault(c.Id), owners.GetValueOrDefault(c.OwnerId, "?"))).ToList();
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync(string? name, string? contactName, string? contactEmail, string? language,
                                                       string? note, bool withCode)
    {
        var me = await _users.GetUserAsync(User);
        if (me is null || (User.IsInRole("Reviewer") && !User.IsInRole("Admin"))) return Forbid();
        if (CustomerService.CheckCustomer(name, contactEmail, language) is not null)
        {
            Notice = "Please check name and email address.";
            NoticeKind = "error";
            await LoadAsync();
            return Page();
        }
        var c = await _customers.CreateCustomerAsync(name!, contactName, contactEmail, language, note, me, withCode);
        return RedirectToPage("/Customer", new { id = c.Id });
    }
}

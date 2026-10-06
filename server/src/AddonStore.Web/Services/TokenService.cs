using System.Security.Cryptography;
using AddonStore.Web.Data;

namespace AddonStore.Web.Services;

public class TokenService
{
    private readonly AppDbContext _db;

    public TokenService(AppDbContext db) => _db = db;

    /// <summary>Creates a token; the plain value is returned exactly once and never stored.</summary>
    public async Task<(string PlainToken, ApiToken Token)> CreateAsync(AppUser user, string name)
    {
        var plain = "ppak_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var token = new ApiToken
        {
            UserId = user.Id,
            Name = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim(),
            TokenHash = Hash(plain),
            Prefix = plain[..12]
        };
        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync();
        return (plain, token);
    }

    /// <summary>Revokes every active API token of a user (after a password reset or change, audit S1.3.1).</summary>
    public static async Task<int> RevokeAllAsync(AppDbContext db, string userId)
    {
        var active = db.ApiTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToList();
        foreach (var t in active) t.RevokedAt = DateTime.UtcNow;
        if (active.Count > 0) await db.SaveChangesAsync();
        return active.Count;
    }

    public static string Hash(string plain) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plain))).ToLowerInvariant();
}

using System.Security.Claims;
using AddonStore.Web.Auth;
using AddonStore.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>Why a Microsoft sign-in is refused; the value is the code in /Account/Login?entra=.</summary>
public enum EntraRejection
{
    None,
    /// <summary>The token is incomplete or the sign-in could not be finished.</summary>
    Failed,
    /// <summary>The account's tenant is not in Entra:AllowedTenants.</summary>
    Tenant,
    /// <summary>No address the tenant vouches for (guest or external account, or none at all).</summary>
    Email,
}

/// <summary>
/// The person behind a validated Entra ID token, reduced to what the store trusts: tenant, object id,
/// an address from a domain the tenant verified, and a display name.
/// </summary>
public sealed record EntraIdentity(string TenantId, string ObjectId, string Email, string Name)
{
    /// <summary>ProviderKey in AspNetUserLogins: stable per person and tenant, never reused (unlike an address).</summary>
    public string Key => $"{TenantId}:{ObjectId}";

    // claims of the trimmed principal that the OpenID Connect handler keeps in the external cookie
    private const string KeyClaim = "entra_key", EmailClaim = "entra_email", NameClaim = "entra_name";

    public ClaimsPrincipal ToPrincipal() => new(new ClaimsIdentity(new[]
    {
        new Claim(KeyClaim, Key), new Claim(EmailClaim, Email), new Claim(NameClaim, Name),
    }, EntraSettings.Scheme));

    public static EntraIdentity? FromPrincipal(ClaimsPrincipal? p)
    {
        var key = p?.FindFirst(KeyClaim)?.Value;
        var email = p?.FindFirst(EmailClaim)?.Value;
        var name = p?.FindFirst(NameClaim)?.Value;
        if (key is null || email is null || name is null) return null;
        var parts = key.Split(':');
        return parts.Length == 2 && Guid.TryParse(parts[0], out _) && Guid.TryParse(parts[1], out _)
            ? new EntraIdentity(parts[0], parts[1], email, name) : null;
    }

    /// <summary>
    /// Reads the claims of an ID token whose signature, issuer, audience, lifetime and nonce the OpenID
    /// Connect handler already validated. Trust rules:
    /// <list type="bullet">
    /// <item>tid must be one of the allowed tenants (and match the issuer);</item>
    /// <item>guests and other external accounts (idp names another tenant or live.com) are refused: their
    /// address is not one the tenant verified;</item>
    /// <item>the address is upn, else preferred_username (both on domains the tenant verified); the email claim
    /// is used only when it is the same address or xms_edov=true says its domain owner is verified.</item>
    /// </list>
    /// </summary>
    public static (EntraIdentity? Identity, EntraRejection Rejection) Read(IEnumerable<Claim> claims, IReadOnlySet<string> allowedTenants)
    {
        var list = claims as IReadOnlyCollection<Claim> ?? claims.ToList();
        string? Get(string type) => list.FirstOrDefault(c => c.Type == type)?.Value?.Trim() is { Length: > 0 } v ? v : null;

        if (!Guid.TryParse(Get("tid"), out var tidGuid) || !Guid.TryParse(Get("oid"), out var oidGuid))
            return (null, EntraRejection.Failed);
        var tid = tidGuid.ToString("D");
        if (!allowedTenants.Contains(tid))
            return (null, EntraRejection.Tenant);
        if (Get("iss") is { } iss && !iss.EndsWith($"/{tid}/v2.0", StringComparison.OrdinalIgnoreCase))
            return (null, EntraRejection.Tenant);
        if (Get("idp") is { } idp && !idp.Contains(tid, StringComparison.OrdinalIgnoreCase))
            return (null, EntraRejection.Email);

        var verified = Usable(Get("upn")) ?? Usable(Get("preferred_username"));
        var email = Usable(Get("email"));
        var address = verified;
        if (email is not null && !string.Equals(email, verified, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Get("xms_edov"), "true", StringComparison.OrdinalIgnoreCase))
            address = email;
        if (address is null)
            return (null, EntraRejection.Email);

        var name = new string((Get("name") ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length == 0) name = address[..address.IndexOf('@')];
        if (name.Length > 100) name = name[..100].Trim();
        return (new EntraIdentity(tid, oidGuid.ToString("D"), address, name), EntraRejection.None);
    }

    /// <summary>A plain mailbox address (no display name, no guest "#EXT#" UPN), at most 200 characters like a registration.</summary>
    private static string? Usable(string? value) =>
        value is { Length: > 2 and <= 200 } && !value.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsWhiteSpace) && value.IndexOf('@') > 0 &&
        System.Net.Mail.MailAddress.TryCreate(value, out var m) && m.Address == value
            ? value : null;
}

/// <summary>
/// Finds or creates the store account for a Microsoft sign-in, without ever creating a second account for an
/// address that already has one:
/// <list type="number">
/// <item>login "Entra"/"{tid}:{oid}" already linked: that account;</item>
/// <item>an account with the same address (normalized, so case does not matter): the login is linked to it;</item>
/// <item>otherwise a new account in the state of a manual registration: pending, role Developer, no password.</item>
/// </list>
/// Roles are never taken from Entra. Whether the account may sign in decides <see cref="Gate"/>.
/// </summary>
public sealed class EntraAccountService
{
    public const string Provider = EntraSettings.Scheme;
    public const string ProviderDisplayName = "Microsoft";

    public enum Kind { Existing, Linked, Created, Failed }
    public sealed record Outcome(Kind Kind, AppUser? User);

    private readonly UserManager<AppUser> _users;

    public EntraAccountService(UserManager<AppUser> users) => _users = users;

    public async Task<Outcome> ResolveAsync(EntraIdentity id)
    {
        var user = await _users.FindByLoginAsync(Provider, id.Key);
        if (user is not null) return new(Kind.Existing, user);

        user = await FindByEmailAsync(id.Email);
        if (user is not null) return await LinkAsync(user, id);

        var created = new AppUser
        {
            UserName = id.Email,
            Email = id.Email,
            DisplayName = await UniqueNameAsync(id.Name),
            Status = UserStatus.Pending,
        };
        var result = await _users.CreateAsync(created);
        if (!result.Succeeded)
        {
            // a second callback created the account a moment ago: link to that one instead
            if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail") &&
                await FindByEmailAsync(id.Email) is { } again)
                return await LinkAsync(again, id);
            return new(Kind.Failed, null);
        }
        await _users.AddToRoleAsync(created, SchemaUpgrade.DefaultRole);
        var link = await _users.AddLoginAsync(created, new UserLoginInfo(Provider, id.Key, ProviderDisplayName));
        if (!link.Succeeded)
        {
            // the same Microsoft account was linked elsewhere in the meantime: no orphan without a way in
            await _users.DeleteAsync(created);
            return await _users.FindByLoginAsync(Provider, id.Key) is { } other ? new(Kind.Existing, other) : new(Kind.Failed, null);
        }
        return new(Kind.Created, created);
    }

    /// <summary>
    /// Whether the account may be signed in, with the same rules and messages as the password sign-in:
    /// null = sign in; otherwise the code for /Account/Login?entra=.
    /// </summary>
    public static string? Gate(AppUser user, bool lockedOut) =>
        lockedOut ? "failed" : user.Status switch
        {
            UserStatus.Active => null,
            UserStatus.Pending => "pending",
            _ => "inactive",
        };

    private async Task<AppUser?> FindByEmailAsync(string email)
    {
        try { return await _users.FindByEmailAsync(email); }
        catch (InvalidOperationException) { return null; }   // two old accounts share the address: link to neither
    }

    private async Task<Outcome> LinkAsync(AppUser user, EntraIdentity id)
    {
        var result = await _users.AddLoginAsync(user, new UserLoginInfo(Provider, id.Key, ProviderDisplayName));
        if (result.Succeeded) return new(Kind.Linked, user);
        return await _users.FindByLoginAsync(Provider, id.Key) is { } other ? new(Kind.Existing, other) : new(Kind.Failed, null);
    }

    /// <summary>Display names are unique ignoring case (audit S1.3.1): "Name", then "Name (2)", "Name (3)" ...</summary>
    private async Task<string> UniqueNameAsync(string name)
    {
        // compared like DisplayNames.TakenAsync: trimmed, culture-independent upper case
        var taken = (await _users.Users.Select(u => u.DisplayName).ToListAsync())
            .Select(n => n.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        bool Free(string n) => !taken.Contains(n.Trim().ToUpperInvariant());
        if (Free(name)) return name;
        for (var i = 2; i < 100; i++)
        {
            var suffix = $" ({i})";
            var candidate = (name.Length + suffix.Length > 100 ? name[..(100 - suffix.Length)].TrimEnd() : name) + suffix;
            if (Free(candidate)) return candidate;
        }
        return $"{(name.Length > 90 ? name[..90] : name)} {Guid.NewGuid().ToString("N")[..8]}";
    }
}

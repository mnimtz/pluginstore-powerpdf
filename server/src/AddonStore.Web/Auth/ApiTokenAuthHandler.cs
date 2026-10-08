using System.Security.Claims;
using System.Text.Encodings.Web;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AddonStore.Web.Auth;

/// <summary>
/// Authenticates "Authorization: Bearer ppak_..." personal tokens.
/// Distinct failure messages (TOKEN_INVALID / TOKEN_REVOKED / USER_NOT_ACTIVE)
/// are surfaced so API clients know what to fix.
/// </summary>
public class ApiTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public new const string Scheme = "ApiToken";

    private readonly AppDbContext _db;

    public ApiTokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, AppDbContext db)
        : base(options, logger, encoder) => _db = db;

    // one writer per token and minute across all parallel requests (S1.13.1)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> Stamped = new();
    private static bool ClaimStamp(int tokenId)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Stamped.GetOrAdd(tokenId, 0);
        return now - last > TimeSpan.TicksPerMinute && Stamped.TryUpdate(tokenId, now, last);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ppak_", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var plain = header["Bearer ".Length..].Trim();
        var hash = TokenService.Hash(plain);

        var token = await _db.ApiTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
        {
            Context.Items["auth_fail"] = "TOKEN_INVALID";
            return AuthenticateResult.Fail("TOKEN_INVALID");
        }
        if (token.RevokedAt is not null)
        {
            Context.Items["auth_fail"] = "TOKEN_REVOKED";
            return AuthenticateResult.Fail("TOKEN_REVOKED");
        }
        if (token.User is null || token.User.Status != UserStatus.Active)
        {
            Context.Items["auth_fail"] = "USER_NOT_ACTIVE";
            return AuthenticateResult.Fail("USER_NOT_ACTIVE");
        }

        // an activity stamp (at most once a minute): a database briefly locked by parallel requests must not fail
        // the request (hardening S1.13.1: 20 parallel calls with one token answered 500 "database is locked")
        if ((token.LastUsedAt is null || token.LastUsedAt < DateTime.UtcNow.AddMinutes(-1)) && ClaimStamp(token.Id))
        {
            token.LastUsedAt = DateTime.UtcNow;
            try { await _db.SaveChangesAsync(); }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
            {
                _db.Entry(token).State = Microsoft.EntityFrameworkCore.EntityState.Unchanged;
            }
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, token.User.Id),
            new(ClaimTypes.Name, token.User.DisplayName),
            new(ClaimTypes.Email, token.User.Email ?? ""),
            new("token_name", token.Name)
        };
        var roles = await (from ur in _db.UserRoles
                           join r in _db.Roles on ur.RoleId equals r.Id
                           where ur.UserId == token.User.Id
                           select r.Name).ToListAsync();
        claims.AddRange(roles.Where(r => r != null).Select(r => new Claim(ClaimTypes.Role, r!)));

        var identity = new ClaimsIdentity(claims, Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.ContentType = "application/json";
        var code = Context.Items.TryGetValue("auth_fail", out var c) ? c?.ToString() : "UNAUTHENTICATED";
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = false,
            error = new
            {
                code,
                message = "Authentication required.",
                hint = "Send 'Authorization: Bearer ppak_...' with a personal token from your profile page. Call GET /api/me to verify the token."
            }
        });
        return Response.WriteAsync(body);
    }
}

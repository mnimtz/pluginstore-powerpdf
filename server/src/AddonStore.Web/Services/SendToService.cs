using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>Effective "Senden an" settings (admin page Senden an → Settings).</summary>
public sealed record SendToConfig(bool Enabled, int RetentionHours, int MaxFileMB, int MaxPendingMB, bool QuickSend,
                                  int InviteExpiryDays, int MaxOpenInvites, int MaxInvitesPerDay, int UndoSeconds)
{
    public object Public() => new
    {
        enabled = Enabled, retentionHours = RetentionHours, maxFileMB = MaxFileMB, maxPendingMB = MaxPendingMB,
        quickSend = QuickSend, inviteExpiryDays = InviteExpiryDays, maxOpenInvites = MaxOpenInvites,
        maxInvitesPerDay = MaxInvitesPerDay, undoSeconds = UndoSeconds
    };
}

/// <summary>Error of a "Senden an" call: code for the add-on, HTTP status.</summary>
public sealed class SendToError : Exception
{
    public string Code { get; }
    public int Status { get; }
    public SendToError(string code, string message, int status = 400) : base(message) { Code = code; Status = status; }
}

/// <summary>
/// "Senden an": devices, invitations with mutual confirmation, contacts,
/// lists, quick targets and transfers. The server never sees content, file
/// names or notes; it checks that sender and recipient are confirmed contacts
/// and keeps the encrypted envelopes until the recipient answers.
/// </summary>
public sealed class SendToService
{
    public const string EnabledKey = "SendTo.Enabled";
    public const int DeclineCooldownDays = 30;
    public const int MaxChunkBytes = 2 * 1024 * 1024;
    /// <summary>Invitation mails to one address per day, from all senders together (S1.17.3).</summary>
    public const int MaxInvitesPerRecipientPerDay = 5;
    public const int MaxQuickPerUser = 20;
    public const int MaxRetentionHours = 720;

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly SendToStorage _storage;
    private readonly AuditService _audit;

    public SendToService(AppDbContext db, SettingsService settings, SendToStorage storage, AuditService audit)
    {
        _db = db; _settings = settings; _storage = storage; _audit = audit;
    }

    // ------------------------------------------------------------ settings

    private async Task<int> IntAsync(string key, int def, int min, int max)
    {
        var v = await _settings.GetAsync("SendTo." + key, "SendTo:" + key);
        return int.TryParse(v, out var n) ? Math.Clamp(n, min, max) : def;
    }

    private async Task<bool> BoolAsync(string key, bool def)
    {
        var v = await _settings.GetAsync("SendTo." + key, "SendTo:" + key);
        return v.Length == 0 ? def : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    /// <summary>
    /// Last known master switch, for the public documents (agent guide, OpenAPI,
    /// endpoint list): "Send to" is only described while it is on.
    /// </summary>
    public static volatile bool DocsVisible;

    public async Task<SendToConfig> ConfigAsync()
    {
        var cfg = await ReadConfigAsync();
        DocsVisible = cfg.Enabled;
        return cfg;
    }

    private async Task<SendToConfig> ReadConfigAsync() => new(
        await BoolAsync("Enabled", false),
        await IntAsync("RetentionHours", 168, 1, MaxRetentionHours),
        await IntAsync("MaxFileMB", 100, 1, 1024),
        await IntAsync("MaxPendingMB", 1024, 1, 10240),
        await BoolAsync("QuickSend", true),
        await IntAsync("InviteExpiryDays", 14, 1, 60),
        await IntAsync("MaxOpenInvites", 20, 1, 200),
        await IntAsync("MaxInvitesPerDay", 30, 1, 500),
        await IntAsync("UndoSeconds", 10, 0, 60));

    /// <summary>Master switch: on AND a document storage is configured.</summary>
    public async Task<bool> EnabledAsync() => (await ConfigAsync()).Enabled && await _storage.IsConfiguredAsync();

    // ------------------------------------------------------------ helpers

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string NewToken(string prefix) =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? NormEmail(string? e)
    {
        e = e?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || e.Length > 254) return null;
        var at = e.IndexOf('@');
        if (at < 1 || at != e.LastIndexOf('@') || e.IndexOf('.', at) < 0 || e.Any(char.IsWhiteSpace) || e.Any(char.IsControl)) return null;
        return e;
    }

    public static bool ValidName(string n) =>
        n.Length is >= 2 and <= 64 && n == n.Trim() && !n.Any(char.IsControl) && !n.Contains('@') &&
        !n.Contains("://", StringComparison.Ordinal) && !n.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
        !n.Any(c => c is '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩');

    private static bool ValidB64(string? s, int len)
    {
        if (s is null) return false;
        try { return Convert.FromBase64String(s).Length == len; } catch (FormatException) { return false; }
    }

    private static (string a, string b) Pair(string x, string y) => string.CompareOrdinal(x, y) < 0 ? (x, y) : (y, x);

    public async Task<bool> AreContactsAsync(string x, string y)
    {
        var (a, b) = Pair(x, y);
        return await _db.SendToContacts.AnyAsync(c => c.UserA == a && c.UserB == b);
    }

    private async Task<bool> IsBlockedAsync(string blocker, string blocked) =>
        await _db.SendToBlocks.AnyAsync(x => x.BlockerId == blocker && x.BlockedId == blocked);

    private async Task AddContactAsync(string x, string y)
    {
        if (x == y || await AreContactsAsync(x, y)) return;
        var (a, b) = Pair(x, y);
        // S1.17.3: two accepted invitations for the same pair (or a second call before SaveChanges) must not add the
        // pair twice: the unique index failed and registration answered 500 for good
        if (_db.SendToContacts.Local.Any(c => c.UserA == a && c.UserB == b)) return;
        // and never between two people where one blocked the other
        if (await IsBlockedAsync(x, y) || await IsBlockedAsync(y, x)) return;
        _db.SendToContacts.Add(new SendToContact { UserA = a, UserB = b });
    }

    /// <summary>The bytes of chunk n that a transfer of this size may have (1 MiB blocks plus the AES-GCM tag).</summary>
    public const int ClientChunk = 1024 * 1024, GcmTag = 16;
    private static int ExpectedChunks(long size) => size == 0 ? 1 : (int)((size + ClientChunk - 1) / ClientChunk);
    private static long MaxChunkLength(long size, int n) =>
        (size == 0 ? 0 : Math.Min(ClientChunk, size - (long)n * ClientChunk)) + GcmTag;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ChunkLocks = new();

    // ------------------------------------------------------------ devices

    public async Task<SendToDevice?> DeviceByTokenAsync(string token)
    {
        var h = Hash(token);
        return await _db.SendToDevices.FirstOrDefaultAsync(d => d.TokenHash == h);
    }

    public async Task TouchAsync(SendToDevice d)
    {
        // At most one write per device and minute (SQLite on a network share).
        if (DateTime.UtcNow - d.LastSeenAt < TimeSpan.FromMinutes(1)) return;
        d.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<(SendToDevice device, string token)> RegisterAsync(string? rawEmail, string? name, string? nameSource,
                                                                          string? kemPub, string? sigPub, int? customerId)
    {
        var email = NormEmail(rawEmail) ?? throw new SendToError("EMAIL_INVALID", "A valid e-mail address is required.");
        name = (name ?? "").Trim();
        if (!ValidName(name)) throw new SendToError("NAME_INVALID", "Name must be 2 to 64 characters, no links or control characters.");
        if (!ValidB64(kemPub, 32) || !ValidB64(sigPub, 65))
            throw new SendToError("KEY_INVALID", "kemPub must be 32 bytes (X25519), sigPub 65 bytes (P-256 uncompressed), base64.");
        var source = nameSource is "identity" or "email" or "windows" or "user" ? nameSource : "email";

        var u = await _db.SendToUsers.FirstOrDefaultAsync(x => x.Email == email);
        if (u is null)
        {
            u = new SendToUser { Email = email, Domain = email[(email.IndexOf('@') + 1)..], Name = name, DerivedName = name, NameSource = source };
            _db.SendToUsers.Add(u);
        }
        else if (u.NameSource != "user")
        {
            u.Name = name; u.DerivedName = name; u.NameSource = source;
        }
        if (u.BlockedAt is not null) throw new SendToError("USER_BLOCKED", "This user is blocked.", 403);
        if (customerId is not null) u.CustomerId = customerId;

        var token = NewToken("stdev_");
        var d = new SendToDevice { UserId = u.Id, KemPub = kemPub!, SigPub = sigPub!, TokenHash = Hash(token) };
        _db.SendToDevices.Add(d);

        // Invitations to this address get the user; accepted ones (web link
        // before installation) become contacts now.
        foreach (var inv in await _db.SendToInvitations.Where(i => i.ToEmail == email && i.ToUserId == null).ToListAsync())
        {
            inv.ToUserId = u.Id;
            if (inv.Status == "accepted") await AddContactAsync(inv.FromUserId, u.Id);
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync(email, "sendto.device.registered", d.Id, $"name source {source}");
        return (d, token);
    }

    public async Task RemoveDeviceAsync(SendToDevice me, string id)
    {
        var d = await _db.SendToDevices.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me.UserId)
                ?? throw new SendToError("DEVICE_NOT_FOUND", "Unknown device.", 404);
        _db.SendToDevices.Remove(d);
        foreach (var e in await _db.SendToEnvelopes.Where(e => e.RecipientDeviceId == d.Id && e.Status == "waiting").ToListAsync())
        { e.Status = "removed"; e.ResolvedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    public async Task<object> MeAsync(SendToDevice dev)
    {
        var u = await _db.SendToUsers.FirstAsync(x => x.Id == dev.UserId);
        var devices = await _db.SendToDevices.Where(d => d.UserId == u.Id)
            .Select(d => new { deviceId = d.Id, created = d.CreatedAt, lastSeen = d.LastSeenAt }).ToListAsync();
        return new { userId = u.Id, email = u.Email, name = u.Name, nameSource = u.NameSource, derivedName = u.DerivedName, deviceId = dev.Id, devices };
    }

    public async Task<string> SetNameAsync(SendToDevice dev, string? name, bool reset)
    {
        var u = await _db.SendToUsers.FirstAsync(x => x.Id == dev.UserId);
        if (reset) { u.Name = u.DerivedName; u.NameSource = "derived"; }
        else
        {
            name = (name ?? "").Trim();
            if (!ValidName(name)) throw new SendToError("NAME_INVALID", "Name must be 2 to 64 characters, no links or control characters.");
            u.Name = name; u.NameSource = "user";
        }
        await _db.SaveChangesAsync();
        return u.Name;
    }

    // ------------------------------------------------------------ contacts

    public async Task<List<object>> ContactsAsync(string userId)
    {
        var rows = await _db.SendToContacts.Where(c => c.UserA == userId || c.UserB == userId).ToListAsync();
        var otherIds = rows.Select(c => c.UserA == userId ? c.UserB : c.UserA).ToList();
        var users = await _db.SendToUsers.Where(u => otherIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var withDevice = (await _db.SendToDevices.Where(d => otherIds.Contains(d.UserId)).Select(d => d.UserId).ToListAsync()).ToHashSet();
        var list = new List<object>();
        foreach (var c in rows)
        {
            var id = c.UserA == userId ? c.UserB : c.UserA;
            if (!users.TryGetValue(id, out var o)) continue;
            var status = o.BlockedAt is not null ? "unreachable" : withDevice.Contains(id) ? "active" : "unreachable";
            list.Add(new { userId = id, email = o.Email, name = o.Name, status, since = c.Since });
        }
        foreach (var inv in await _db.SendToInvitations.Where(i => i.FromUserId == userId && i.Status == "accepted" && i.ToUserId == null).ToListAsync())
            list.Add(new { userId = (string?)null, email = inv.ToEmail, name = inv.ToEmail, status = "pending_setup", since = inv.AnsweredAt,
                           invitationId = inv.Id });   // S1.17.2: withdrawable (DELETE /api/sendto/invitations/{id})
        return list;
    }

    public async Task RemoveContactAsync(string me, string other)
    {
        var (a, b) = Pair(me, other);
        var row = await _db.SendToContacts.FirstOrDefaultAsync(c => c.UserA == a && c.UserB == b)
                  ?? throw new SendToError("CONTACT_NOT_FOUND", "Not a contact.", 404);
        _db.SendToContacts.Remove(row);
        await DropBetweenAsync(me, other);
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    // Waiting transfers between the two, list members and quick targets end
    // with the contact, in both directions.
    private async Task DropBetweenAsync(string x, string y)
    {
        var sentIds = await _db.SendToTransfers.Where(t => t.SenderUserId == x || t.SenderUserId == y).Select(t => new { t.Id, t.SenderUserId }).ToListAsync();
        var byX = sentIds.Where(t => t.SenderUserId == x).Select(t => t.Id).ToList();
        var byY = sentIds.Where(t => t.SenderUserId == y).Select(t => t.Id).ToList();
        var envs = await _db.SendToEnvelopes.Where(e => e.Status == "waiting" &&
            ((byX.Contains(e.TransferId) && e.RecipientUserId == y) || (byY.Contains(e.TransferId) && e.RecipientUserId == x))).ToListAsync();
        foreach (var e in envs) { e.Status = "removed"; e.ResolvedAt = DateTime.UtcNow; }
        foreach (var l in await _db.SendToLists.Where(l => l.OwnerId == x || l.OwnerId == y).ToListAsync())
        {
            var m = Members(l);
            if (m.Remove(l.OwnerId == x ? y : x)) { l.MembersJson = JsonSerializer.Serialize(m); l.UpdatedAt = DateTime.UtcNow; }
        }
    }

    public async Task BlockAsync(string me, string other, string? reportReason)
    {
        // S1.17.3: only about users that exist (before, any id filled the blocks and reports tables)
        if (me == other || !await _db.SendToUsers.AnyAsync(u => u.Id == other))
            throw new SendToError("CONTACT_NOT_FOUND", "Unknown user.", 404);
        var (a, b) = Pair(me, other);
        var row = await _db.SendToContacts.FirstOrDefaultAsync(c => c.UserA == a && c.UserB == b);
        if (row is not null) _db.SendToContacts.Remove(row);
        await DropBetweenAsync(me, other);
        if (!await IsBlockedAsync(me, other)) _db.SendToBlocks.Add(new SendToBlock { BlockerId = me, BlockedId = other });
        foreach (var i in await _db.SendToInvitations.Where(i => i.Status == "open" && i.FromUserId == other && i.ToUserId == me).ToListAsync())
        { i.Status = "declined"; i.AnsweredAt = DateTime.UtcNow; }
        // S1.17.3: also the blocker's own open invitation to the blocked user: accepting it made them contacts again
        var otherUser = await _db.SendToUsers.FirstOrDefaultAsync(u => u.Id == other);
        foreach (var i in await _db.SendToInvitations.Where(i => i.Status == "open" && i.FromUserId == me
                     && (i.ToUserId == other || (otherUser != null && i.ToEmail == otherUser.Email))).ToListAsync())
            i.Status = "withdrawn";
        if (reportReason is not null)
            _db.SendToReports.Add(new SendToReport { ReporterId = me, ReportedId = other, Reason = reportReason.Length > 500 ? reportReason[..500] : reportReason });
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    public async Task<List<object>> DevicesOfContactAsync(string me, string other)
    {
        if (!await AreContactsAsync(me, other)) throw new SendToError("NOT_A_CONTACT", "Keys are only shared between confirmed contacts.", 403);
        return (await _db.SendToDevices.Where(d => d.UserId == other).ToListAsync())
            .Select(d => (object)new { deviceId = d.Id, kemPub = d.KemPub, sigPub = d.SigPub }).ToList();
    }

    // ------------------------------------------------------------ invitations

    /// <summary>One invitation. Mail to send is returned (the caller sends it).</summary>
    public async Task<(object result, (SendToInvitation inv, string token, SendToUser from)? mail)> InviteAsync(string fromUserId, string raw, string lang)
    {
        var cfg = await ConfigAsync();
        var email = NormEmail(raw);
        object R(string status, string? code = null) => new { email = email ?? raw, status, code };
        if (email is null) return (R("error", "EMAIL_INVALID"), null);
        var me = await _db.SendToUsers.FirstAsync(u => u.Id == fromUserId);
        if (email == me.Email) return (R("error", "INVITE_SELF"), null);
        var target = await _db.SendToUsers.FirstOrDefaultAsync(u => u.Email == email);
        if (target is not null && await AreContactsAsync(me.Id, target.Id)) return (R("error", "ALREADY_CONTACT"), null);

        // Mutual: the other side already invited me, so this is the answer.
        if (target is not null)
        {
            var now = DateTime.UtcNow;
            var reverse = await _db.SendToInvitations.FirstOrDefaultAsync(i => i.Status == "open" && i.FromUserId == target.Id && i.ToEmail == me.Email && i.ExpiresAt > now);
            if (reverse is not null && !await IsBlockedAsync(me.Id, target.Id) && !await IsBlockedAsync(target.Id, me.Id))
            {
                reverse.Status = "accepted"; reverse.AnsweredAt = DateTime.UtcNow; reverse.ToUserId = me.Id;
                await AddContactAsync(target.Id, me.Id);
                await _db.SaveChangesAsync();
                return (R("contact"), null);
            }
        }

        if (await _db.SendToInvitations.AnyAsync(i => (i.Status == "open" || (i.Status == "accepted" && i.ToUserId == null))
                                                       && i.FromUserId == me.Id && i.ToEmail == email))
            return (R("error", "ALREADY_INVITED"), null);
        // S1.17.3: at most a few invitation mails per recipient and day, whoever sends them
        var dayBefore = DateTime.UtcNow.AddDays(-1);
        if (await _db.SendToInvitations.CountAsync(i => i.ToEmail == email && i.CreatedAt > dayBefore) >= MaxInvitesPerRecipientPerDay)
            return (R("error", "TOO_MANY_INVITES_TODAY"), null);
        var cooldown = DateTime.UtcNow.AddDays(-DeclineCooldownDays);
        if (await _db.SendToInvitations.AnyAsync(i => i.Status == "declined" && i.FromUserId == me.Id && i.ToEmail == email && i.AnsweredAt > cooldown))
            return (R("error", "INVITE_COOLDOWN"), null);
        if (await _db.SendToInvitations.CountAsync(i => i.Status == "open" && i.FromUserId == me.Id) >= cfg.MaxOpenInvites)
            return (R("error", "TOO_MANY_OPEN_INVITES"), null);
        var dayAgo = DateTime.UtcNow.AddDays(-1);
        if (await _db.SendToInvitations.CountAsync(i => i.FromUserId == me.Id && i.CreatedAt > dayAgo) >= cfg.MaxInvitesPerDay)
            return (R("error", "TOO_MANY_INVITES_TODAY"), null);

        var token = NewToken("");
        var inv = new SendToInvitation
        {
            FromUserId = me.Id, ToEmail = email, ToUserId = target?.Id, TokenHash = Hash(token),
            ExpiresAt = DateTime.UtcNow.AddDays(cfg.InviteExpiryDays), Lang = Lang.Ui.Contains(lang) ? lang : "en"
        };
        _db.SendToInvitations.Add(inv);
        await _db.SaveChangesAsync();
        // A blocked inviter gets the same answer, but no mail goes out.
        var silent = target is not null && await IsBlockedAsync(target.Id, me.Id);
        return (R("invited"), silent ? null : (inv, token, me));
    }

    public async Task<object> InvitationsAsync(string userId)
    {
        var me = await _db.SendToUsers.FirstAsync(u => u.Id == userId);
        var sent = await _db.SendToInvitations.Where(i => i.FromUserId == userId && (i.Status == "open" || i.Status == "declined" || i.Status == "expired"))
            .OrderByDescending(i => i.CreatedAt).Take(100)
            .Select(i => new { invitationId = i.Id, toEmail = i.ToEmail, status = i.Status, created = i.CreatedAt, expiresAt = i.ExpiresAt }).ToListAsync();
        var blocked = await _db.SendToBlocks.Where(x => x.BlockerId == userId).Select(x => x.BlockedId).ToListAsync();
        var rec = await _db.SendToInvitations.Where(i => i.Status == "open" && (i.ToUserId == userId || i.ToEmail == me.Email) && !blocked.Contains(i.FromUserId))
            .OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync();
        var fromIds = rec.Select(i => i.FromUserId).ToList();
        var from = await _db.SendToUsers.Where(u => fromIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        var received = rec.Where(i => from.ContainsKey(i.FromUserId)).Select(i => new
        {
            invitationId = i.Id, fromUserId = i.FromUserId, fromName = from[i.FromUserId].Name, fromEmail = from[i.FromUserId].Email,
            created = i.CreatedAt, expiresAt = i.ExpiresAt
        }).ToList();
        return new { sent, received };
    }

    public async Task AnswerAsync(string id, string userId, bool accept)
    {
        var me = await _db.SendToUsers.FirstAsync(u => u.Id == userId);
        var inv = await _db.SendToInvitations.FirstOrDefaultAsync(i => i.Id == id && (i.ToUserId == userId || i.ToEmail == me.Email))
                  ?? throw new SendToError("INVITATION_NOT_FOUND", "Unknown invitation.", 404);
        if (inv.Status != "open" || inv.ExpiresAt < DateTime.UtcNow) throw new SendToError("INVITATION_NOT_OPEN", "The invitation is not open.");
        inv.ToUserId = userId;
        await SettleAsync(inv, accept);
    }

    public async Task<SendToInvitation?> InvitationByTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var h = Hash(token);
        return await _db.SendToInvitations.FirstOrDefaultAsync(i => i.TokenHash == h);
    }

    public async Task<SendToUser?> UserAsync(string id) => await _db.SendToUsers.FirstOrDefaultAsync(u => u.Id == id);

    /// <summary>Answer from the web page (POST with the button).</summary>
    public async Task<bool> AnswerByTokenAsync(SendToInvitation inv, bool accept)
    {
        if (inv.Status != "open" || inv.ExpiresAt < DateTime.UtcNow) return false;
        await SettleAsync(inv, accept);
        return true;
    }

    private async Task SettleAsync(SendToInvitation inv, bool accept)
    {
        inv.Status = accept ? "accepted" : "declined";
        inv.AnsweredAt = DateTime.UtcNow;
        if (accept && inv.ToUserId is not null) await AddContactAsync(inv.FromUserId, inv.ToUserId);
        await _db.SaveChangesAsync();
    }

    public async Task WithdrawAsync(string id, string userId)
    {
        var inv = await _db.SendToInvitations.FirstOrDefaultAsync(i => i.Id == id && i.FromUserId == userId)
                  ?? throw new SendToError("INVITATION_NOT_FOUND", "Unknown invitation.", 404);
        // S1.17.2: also one accepted by link whose invitee never set up a device (shown as "pending_setup"):
        // it would otherwise stay on the inviter's list for good
        var pendingSetup = inv.Status == "accepted" && inv.ToUserId is null;
        if (inv.Status != "open" && !pendingSetup) throw new SendToError("INVITATION_NOT_OPEN", "The invitation is not open.");
        inv.Status = "withdrawn";
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ lists / quick targets

    private static List<string> Members(SendToList l)
    {
        try { return JsonSerializer.Deserialize<List<string>>(l.MembersJson) ?? new(); } catch (JsonException) { return new(); }
    }

    private static object ListView(SendToList l) => new { listId = l.Id, name = l.Name, favorite = l.Favorite, members = Members(l) };

    public async Task<List<object>> ListsAsync(string owner) =>
        (await _db.SendToLists.Where(l => l.OwnerId == owner).ToListAsync()).Select(ListView).ToList();

    public async Task<object> SaveListAsync(string owner, string? id, string? name, List<string>? members, bool favorite)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new SendToError("NAME_INVALID", "List name 1 to 64 characters.");
        members = (members ?? new()).Distinct().Take(200).ToList();
        foreach (var m in members)
            if (!await AreContactsAsync(owner, m)) throw new SendToError("MEMBER_NOT_A_CONTACT", "Lists hold confirmed contacts only.");
        SendToList? l;
        if (id is null)
        {
            if (await _db.SendToLists.CountAsync(x => x.OwnerId == owner) >= 100) throw new SendToError("TOO_MANY", "At most 100 lists.");
            l = new SendToList { OwnerId = owner };
            _db.SendToLists.Add(l);
        }
        else l = await _db.SendToLists.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner) ?? throw new SendToError("LIST_NOT_FOUND", "Unknown list.", 404);
        l.Name = name; l.MembersJson = JsonSerializer.Serialize(members); l.Favorite = favorite; l.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ListView(l);
    }

    public async Task DeleteListAsync(string owner, string id)
    {
        var l = await _db.SendToLists.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner) ?? throw new SendToError("LIST_NOT_FOUND", "Unknown list.", 404);
        _db.SendToLists.Remove(l);
        await _db.SaveChangesAsync();
    }

    private async Task<bool> QuickValidAsync(string owner, SendToQuick q) =>
        q.TargetType == "user" ? await AreContactsAsync(owner, q.TargetId) : await _db.SendToLists.AnyAsync(l => l.Id == q.TargetId && l.OwnerId == owner);

    public async Task<List<object>> QuickAsync(string owner)
    {
        var list = new List<object>();
        foreach (var q in await _db.SendToQuicks.Where(q => q.OwnerId == owner).OrderBy(q => q.SortOrder).ToListAsync())
            list.Add(new { quickId = q.Id, targetType = q.TargetType, targetId = q.TargetId, label = q.Label, note = q.Note, shortcut = q.Shortcut,
                           inRibbon = q.InRibbon, order = q.SortOrder, valid = await QuickValidAsync(owner, q) });
        return list;
    }

    public async Task<object> SaveQuickAsync(string owner, string? id, JsonElement b)
    {
        string S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool? B(string k) => b.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        SendToQuick? q;
        if (id is null)
        {
            if (await _db.SendToQuicks.CountAsync(x => x.OwnerId == owner) >= MaxQuickPerUser) throw new SendToError("TOO_MANY", $"At most {MaxQuickPerUser} quick targets.");
            q = new SendToQuick { OwnerId = owner, SortOrder = await _db.SendToQuicks.CountAsync(x => x.OwnerId == owner) };
        }
        else q = await _db.SendToQuicks.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner) ?? throw new SendToError("QUICK_NOT_FOUND", "Unknown quick target.", 404);
        q.TargetType = S("targetType") == "list" ? "list" : "user";
        q.TargetId = S("targetId");
        q.Label = S("label").Trim();
        q.Note = S("note");
        q.Shortcut = S("shortcut");
        q.InRibbon = B("inRibbon") ?? true;
        if (b.TryGetProperty("order", out var o) && o.TryGetInt32(out var ord)) q.SortOrder = ord;
        if (q.Label.Length is < 1 or > 32 || q.Label.Any(char.IsControl)) throw new SendToError("LABEL_INVALID", "Label 1 to 32 characters.");
        if (q.Note.Length > 500) throw new SendToError("NOTE_TOO_LONG", "Note at most 500 characters.");
        if (!await QuickValidAsync(owner, q)) throw new SendToError("TARGET_INVALID", "Target must be a confirmed contact or an own list.");
        if (id is null) _db.SendToQuicks.Add(q);
        await _db.SaveChangesAsync();
        return new { quickId = q.Id, targetType = q.TargetType, targetId = q.TargetId, label = q.Label, note = q.Note, shortcut = q.Shortcut, inRibbon = q.InRibbon, order = q.SortOrder };
    }

    public async Task DeleteQuickAsync(string owner, string id)
    {
        var q = await _db.SendToQuicks.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner) ?? throw new SendToError("QUICK_NOT_FOUND", "Unknown quick target.", 404);
        _db.SendToQuicks.Remove(q);
        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ transfers

    public sealed record EnvelopeIn(string DeviceId, string Envelope, string Signature);

    public async Task<(string id, DateTime deliverAfter)> CreateTransferAsync(SendToDevice dev, long size, int chunks, List<EnvelopeIn>? envs)
    {
        var cfg = await ConfigAsync();
        if (size < 0 || chunks < 1 || chunks > 2000 || envs is null || envs.Count == 0 || envs.Count > 500)
            throw new SendToError("TRANSFER_INVALID", "size, chunkCount and envelopes[] are required.");
        // S1.17.3: the quota counts the declared size, so it must match the blocks (1 MiB each): before, size 0 with
        // 2000 blocks of 2 MB stored about 4 GB outside every limit
        if (chunks != ExpectedChunks(size))
            throw new SendToError("TRANSFER_INVALID", $"chunkCount must be ceil(size / {ClientChunk}) (1 for an empty file).");
        if (size > (long)cfg.MaxFileMB * 1024 * 1024) throw new SendToError("FILE_TOO_LARGE", $"Maximum is {cfg.MaxFileMB} MB.");
        var pending = await _db.SendToTransfers.Where(t => t.SenderUserId == dev.UserId && t.PurgedAt == null).SumAsync(t => (long?)t.Size) ?? 0;
        if (pending + size > (long)cfg.MaxPendingMB * 1024 * 1024) throw new SendToError("PENDING_QUOTA", $"Maximum {cfg.MaxPendingMB} MB waiting.");

        var t = new SendToTransfer
        {
            SenderUserId = dev.UserId, SenderDeviceId = dev.Id, Size = size, ChunkCount = chunks,
            DeliverAfter = DateTime.UtcNow.AddSeconds(cfg.UndoSeconds), ExpiresAt = DateTime.UtcNow.AddHours(cfg.RetentionHours)
        };
        foreach (var e in envs)
        {
            var rd = await _db.SendToDevices.FirstOrDefaultAsync(d => d.Id == e.DeviceId) ?? throw new SendToError("DEVICE_NOT_FOUND", "Unknown recipient device.");
            if (!await AreContactsAsync(dev.UserId, rd.UserId) || await IsBlockedAsync(rd.UserId, dev.UserId))
                throw new SendToError("NOT_A_CONTACT", "Recipient is not a confirmed contact.");
            if (e.Envelope is null || e.Envelope.Length is < 40 or > 8192 || e.Signature is null || e.Signature.Length > 200)
                throw new SendToError("ENVELOPE_INVALID", "Envelope or signature has an invalid size.");
            _db.SendToEnvelopes.Add(new SendToEnvelope
            {
                TransferId = t.Id, RecipientUserId = rd.UserId, RecipientDeviceId = rd.Id, Data = e.Envelope, Signature = e.Signature
            });
        }
        _db.SendToTransfers.Add(t);
        await _db.SaveChangesAsync();
        return (t.Id, t.DeliverAfter);
    }

    private static HashSet<int> Uploaded(SendToTransfer t) =>
        t.Uploaded.Length == 0 ? new() : t.Uploaded.Split(',').Select(int.Parse).ToHashSet();

    public async Task PutChunkAsync(SendToDevice dev, string id, int n, byte[] data)
    {
        var t = await _db.SendToTransfers.FirstOrDefaultAsync(x => x.Id == id && x.SenderDeviceId == dev.Id);
        if (t is null || t.PurgedAt is not null || t.Cancelled) throw new SendToError("TRANSFER_NOT_FOUND", "Unknown transfer.", 404);
        if (n < 0 || n >= t.ChunkCount) throw new SendToError("CHUNK_INDEX", "Chunk index out of range.");
        if (data.Length == 0 || data.Length > MaxChunkBytes || data.Length > MaxChunkLength(t.Size, n))
            throw new SendToError("CHUNK_SIZE", "Chunk size invalid.");
        // S1.17.3: one upload at a time per transfer (parallel ones lost an index), and no block changes once all are in
        var gate = ChunkLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await _db.Entry(t).ReloadAsync();
            var up = Uploaded(t);
            if (up.Count == t.ChunkCount) return;   // complete: a repeated block (lost answer) changes nothing
            await _storage.PutAsync(id, n, data);
            if (up.Add(n))
            {
                t.Uploaded = string.Join(',', up.OrderBy(x => x));
                // the undo window starts when the last block is in (it started at creation, so a slow upload had none)
                if (up.Count == t.ChunkCount) t.DeliverAfter = DateTime.UtcNow.AddSeconds((await ConfigAsync()).UndoSeconds);
                await _db.SaveChangesAsync();
            }
        }
        finally
        {
            gate.Release();
            if (ChunkLocks.Count > 1000) ChunkLocks.TryRemove(id, out _);
        }
    }

    public async Task CancelAsync(SendToDevice dev, string id)
    {
        var t = await _db.SendToTransfers.FirstOrDefaultAsync(x => x.Id == id && x.SenderUserId == dev.UserId)
                ?? throw new SendToError("TRANSFER_NOT_FOUND", "Unknown transfer.", 404);
        if (DateTime.UtcNow >= t.DeliverAfter && Uploaded(t).Count == t.ChunkCount) throw new SendToError("ALREADY_DELIVERED", "The undo window has passed.");
        t.Cancelled = true;
        foreach (var e in await _db.SendToEnvelopes.Where(e => e.TransferId == id && e.Status == "waiting").ToListAsync())
        { e.Status = "cancelled"; e.ResolvedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    private static bool Deliverable(SendToTransfer t) =>
        !t.Cancelled && t.PurgedAt is null && DateTime.UtcNow >= t.DeliverAfter && t.ExpiresAt > DateTime.UtcNow && Uploaded(t).Count == t.ChunkCount;

    public async Task<List<object>> InboxAsync(SendToDevice dev)
    {
        var envs = await _db.SendToEnvelopes.Where(e => e.RecipientDeviceId == dev.Id && e.Status == "waiting").ToListAsync();
        var tIds = envs.Select(e => e.TransferId).ToList();
        var ts = await _db.SendToTransfers.Where(t => tIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var sIds = ts.Values.Select(t => t.SenderUserId).ToList();
        var senders = await _db.SendToUsers.Where(u => sIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        return envs.Where(e => ts.ContainsKey(e.TransferId) && Deliverable(ts[e.TransferId]) && senders.ContainsKey(ts[e.TransferId].SenderUserId))
            .Select(e =>
            {
                var t = ts[e.TransferId];
                var s = senders[t.SenderUserId];
                return (object)new
                {
                    transferId = t.Id, senderUserId = s.Id, senderName = s.Name, senderEmail = s.Email, senderDeviceId = t.SenderDeviceId,
                    size = t.Size, chunkCount = t.ChunkCount, envelope = e.Data, signature = e.Signature, created = t.CreatedAt, expiresAt = t.ExpiresAt
                };
            }).ToList();
    }

    public async Task<List<object>> SentAsync(string userId)
    {
        var ts = await _db.SendToTransfers.Where(t => t.SenderUserId == userId).OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync();
        var ids = ts.Select(t => t.Id).ToList();
        var envs = await _db.SendToEnvelopes.Where(e => ids.Contains(e.TransferId)).ToListAsync();
        var rIds = envs.Select(e => e.RecipientUserId).Distinct().ToList();
        var users = await _db.SendToUsers.Where(u => rIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        return ts.Select(t => (object)new
        {
            transferId = t.Id, size = t.Size, created = t.CreatedAt, expiresAt = t.ExpiresAt, cancelled = t.Cancelled,
            uploaded = Uploaded(t).Count == t.ChunkCount,
            recipients = envs.Where(e => e.TransferId == t.Id).GroupBy(e => e.RecipientUserId).Select(g =>
            {
                users.TryGetValue(g.Key, out var u);
                var st = g.Any(e => e.Status == "accepted") ? "accepted"
                       : g.Any(e => e.Status == "declined") ? "declined"
                       : g.All(e => e.Status == "waiting") ? (Deliverable(t) ? "delivered" : "sending")
                       : g.First().Status;
                return new { userId = g.Key, name = u?.Name, email = u?.Email, status = st };
            }).ToList()
        }).ToList();
    }

    public async Task<byte[]?> GetChunkAsync(SendToDevice dev, string id, int n)
    {
        var e = await _db.SendToEnvelopes.FirstOrDefaultAsync(e => e.TransferId == id && e.RecipientDeviceId == dev.Id && e.Status == "waiting");
        var t = await _db.SendToTransfers.FirstOrDefaultAsync(t => t.Id == id);
        if (e is null || t is null || !Deliverable(t) || n < 0 || n >= t.ChunkCount) return null;
        return await _storage.GetAsync(id, n);
    }

    /// <summary>Accept or decline counts for the user, i.e. every device of theirs.</summary>
    public async Task ResolveAsync(SendToDevice dev, string id, string status)
    {
        var mine = await _db.SendToEnvelopes.Where(e => e.TransferId == id && e.RecipientUserId == dev.UserId && e.Status == "waiting").ToListAsync();
        if (mine.Count == 0) throw new SendToError("TRANSFER_NOT_FOUND", "Unknown transfer.", 404);
        foreach (var e in mine) { e.Status = status; e.ResolvedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    public async Task<object> EventsAsync(SendToDevice dev)
    {
        var me = await _db.SendToUsers.FirstAsync(u => u.Id == dev.UserId);
        var inbox = (await InboxAsync(dev)).Count;
        var blocked = await _db.SendToBlocks.Where(x => x.BlockerId == me.Id).Select(x => x.BlockedId).ToListAsync();
        var requests = await _db.SendToInvitations.CountAsync(i => i.Status == "open" && (i.ToUserId == me.Id || i.ToEmail == me.Email) && !blocked.Contains(i.FromUserId));
        return new { inbox, requests, config = (await ConfigAsync()).Public() };
    }

    // ------------------------------------------------------------ expiry / purge / switch off

    /// <summary>Expires invitations and transfers, deletes finished blocks. Returns purged transfers.</summary>
    public async Task<int> SweepAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var i in await _db.SendToInvitations.Where(i => i.Status == "open" && i.ExpiresAt < now).ToListAsync()) i.Status = "expired";
        var expired = await _db.SendToTransfers.Where(t => t.PurgedAt == null && t.ExpiresAt < now).Select(t => t.Id).ToListAsync();
        foreach (var e in await _db.SendToEnvelopes.Where(e => expired.Contains(e.TransferId) && e.Status == "waiting").ToListAsync())
        { e.Status = "expired"; e.ResolvedAt = now; }
        // Invitations of non-users are kept only until they expire (+1 day for the answer page).
        var gone = now.AddDays(-1);
        // S1.17.3: declined ones stay through the 30-day cooldown, or the inviter could invite again after 15 days
        var cooled = now.AddDays(-DeclineCooldownDays);
        _db.SendToInvitations.RemoveRange(await _db.SendToInvitations.Where(i => i.ToUserId == null && i.Status != "open" && i.Status != "accepted"
            && i.ExpiresAt < gone && (i.Status != "declined" || i.AnsweredAt == null || i.AnsweredAt < cooled)).ToListAsync());
        await _db.SaveChangesAsync();
        var purged = await PurgeFinishedAsync();
        try
        {
            var open = (await _db.SendToTransfers.Where(t => t.PurgedAt == null).Select(t => t.Id).ToListAsync()).ToHashSet();
            purged += await _storage.DeleteOrphansAsync(open, now.AddHours(-1));
        }
        catch (Exception) { }   // storage unreachable: the next sweep tries again
        return purged;
    }

    /// <summary>Deletes the blocks of every transfer no envelope waits for any more.</summary>
    public async Task<int> PurgeFinishedAsync()
    {
        var open = await _db.SendToTransfers.Where(t => t.PurgedAt == null).ToListAsync();
        var waiting = (await _db.SendToEnvelopes.Where(e => e.Status == "waiting").Select(e => e.TransferId).Distinct().ToListAsync()).ToHashSet();
        var n = 0;
        foreach (var t in open.Where(t => !waiting.Contains(t.Id)))
        {
            try { await _storage.DeleteTransferAsync(t.Id); } catch (Exception) { continue; }   // retried by the next sweep
            t.PurgedAt = DateTime.UtcNow;
            n++;
        }
        if (n > 0) await _db.SaveChangesAsync();
        return n;
    }

    /// <summary>Master switch off: every waiting transfer is deleted (concept: senders see "not delivered").</summary>
    public async Task DropAllWaitingAsync()
    {
        foreach (var e in await _db.SendToEnvelopes.Where(e => e.Status == "waiting").ToListAsync()) { e.Status = "expired"; e.ResolvedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
    }

    // ------------------------------------------------------------ admin

    public async Task<object> OverviewAsync()
    {
        var (bytes, blobs) = await _storage.UsageAsync();
        return new
        {
            users = await _db.SendToUsers.CountAsync(),
            devices = await _db.SendToDevices.CountAsync(),
            contacts = await _db.SendToContacts.CountAsync(),
            openInvitations = await _db.SendToInvitations.CountAsync(i => i.Status == "open"),
            waitingTransfers = await _db.SendToTransfers.CountAsync(t => t.PurgedAt == null),
            openReports = await _db.SendToReports.CountAsync(r => r.Status == "open"),
            storageBytes = bytes, storageBlobs = blobs
        };
    }

    public async Task SetUserBlockedAsync(string userId, bool blocked, string admin)
    {
        var u = await _db.SendToUsers.FirstOrDefaultAsync(x => x.Id == userId) ?? throw new SendToError("USER_NOT_FOUND", "Unknown user.", 404);
        u.BlockedAt = blocked ? DateTime.UtcNow : null;
        u.BlockedBy = blocked ? admin : null;
        if (blocked)
        {
            var mine = await _db.SendToTransfers.Where(t => t.SenderUserId == userId && t.PurgedAt == null).Select(t => t.Id).ToListAsync();
            foreach (var e in await _db.SendToEnvelopes.Where(e => mine.Contains(e.TransferId) && e.Status == "waiting").ToListAsync())
            { e.Status = "removed"; e.ResolvedAt = DateTime.UtcNow; }
        }
        await _db.SaveChangesAsync();
        await PurgeFinishedAsync();
        await _audit.LogAsync(admin, blocked ? "sendto.user.blocked" : "sendto.user.unblocked", u.Email, "");
    }
}

/// <summary>Expires invitations and transfers and deletes finished blocks every five minutes.</summary>
public sealed class SendToMaintenance : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SendToMaintenance> _log;

    public SendToMaintenance(IServiceScopeFactory scopes, ILogger<SendToMaintenance> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stop); } catch (TaskCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<SendToService>();
                var n = await svc.SweepAsync();
                if (n > 0) _log.LogInformation("sendto: blocks of {Count} finished transfers deleted", n);
            }
            catch (Exception ex) { _log.LogWarning(ex, "sendto: maintenance failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (TaskCanceledException) { return; }
        }
    }
}

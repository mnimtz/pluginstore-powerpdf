using System.Security.Cryptography;
using System.Text;
using AddonStore.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace AddonStore.Web.Services;

/// <summary>
/// One-time token for the first-run setup. Without it, whoever reached a fresh
/// instance first became its administrator. While no account exists, a token
/// is created at start-up (or taken from the setting Setup:Token), written to
/// the server log and to data/setup-token.txt, and is valid for 24 hours; a
/// restart after that creates a new one. The file is deleted once the first
/// administrator exists.
/// </summary>
public sealed class SetupGate
{
    private static readonly TimeSpan Validity = TimeSpan.FromHours(24);
    private readonly string _file;
    private readonly string? _configured;
    private readonly ILogger<SetupGate> _log;
    private string? _token;
    private DateTime _createdUtc;

    /// <summary>Serializes "is there an account yet?" with creating the first one.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public SetupGate(IConfiguration config, IWebHostEnvironment env, ILogger<SetupGate> log)
    {
        var data = config["Storage:Data"];
        if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(env.ContentRootPath, "data");
        _file = Path.Combine(data, "setup-token.txt");
        _configured = config["Setup:Token"];
        _log = log;
    }

    /// <summary>Start-up: creates the token when the instance has no account yet.</summary>
    public async Task InitAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) { Complete(); return; }
        if (!string.IsNullOrWhiteSpace(_configured) && _configured.Trim().Length >= 16)
        {
            _token = _configured.Trim();
            _createdUtc = DateTime.UtcNow;
            _log.LogWarning("First-run setup: use the token from the setting Setup:Token on /Setup.");
            return;
        }
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        _createdUtc = DateTime.UtcNow;
        try
        {
            File.WriteAllText(_file, $"{_token}\nCreated {_createdUtc:yyyy-MM-dd HH:mm} UTC, valid 24 hours. Restart the app for a new one.\n");
        }
        catch (IOException ex) { _log.LogWarning(ex, "setup-token.txt could not be written"); }
        _log.LogWarning("First-run setup token (valid 24 hours, also in data/setup-token.txt): {Token}", _token);
    }

    public bool Check(string? token)
    {
        if (_token is null || string.IsNullOrWhiteSpace(token) || DateTime.UtcNow - _createdUtc > Validity) return false;
        // case-insensitive (hex), compared in constant time
        var a = Encoding.UTF8.GetBytes(_token.ToUpperInvariant());
        var b = Encoding.UTF8.GetBytes(token.Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Complete()
    {
        _token = null;
        try { if (File.Exists(_file)) File.Delete(_file); } catch (IOException) { }
    }
}

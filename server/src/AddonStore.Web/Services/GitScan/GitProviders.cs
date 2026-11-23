using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AddonStore.Web.Services.GitScan;

/// <summary>What the store needs from a git server; one implementation per kind of server (S2.8.0).</summary>
public interface IGitProvider
{
    string Key { get; }
    string Label { get; }
    string DefaultApiUrl { get; }
    /// <summary>Hint for the address field (e.g. https://git.example.com/api/v3).</summary>
    string ApiUrlHint { get; }
    /// <summary>Connection test: sign-in, rights, what the store can see. Lines for the page, the first one the result.</summary>
    Task<GitTestResult> TestAsync(GitConnection c, HttpClient http, CancellationToken ct);
    Task<List<string>> ListReposAsync(GitConnection c, HttpClient http, CancellationToken ct);
    /// <summary>Checks a webhook delivery's signature; event name and delivery id from the headers.</summary>
    bool VerifyWebhook(IHeaderDictionary headers, byte[] body, string secret, out string evt, out string delivery);
}

/// <summary>Connection values with the secrets already decrypted (never logged, never shown).</summary>
public record GitConnection(string ApiUrl, string AuthMode, string AppId, string InstallationId, string? AppKeyPem, string? Token, string Owner);

public record GitTestResult(bool Ok, List<string> Lines);

public static class GitProviders
{
    public static readonly IGitProvider[] All =
    {
        new GitHubProvider("github", "GitHub.com", "https://api.github.com", "https://api.github.com"),
        new GitHubProvider("github-enterprise", "GitHub Enterprise Server", "", "https://git.example.com/api/v3"),
    };

    public static IGitProvider? Find(string key) => All.FirstOrDefault(p => p.Key == key);
}

/// <summary>
/// GitHub (github.com and GitHub Enterprise Server use the same REST API under another address). Sign-in as a
/// GitHub App (recommended: short-lived installation tokens, rights per repository) or with a fine-grained token.
/// No third-party code: the app's JWT is built with .NET's RSA.
/// </summary>
public sealed class GitHubProvider : IGitProvider
{
    public string Key { get; }
    public string Label { get; }
    public string DefaultApiUrl { get; }
    public string ApiUrlHint { get; }

    public GitHubProvider(string key, string label, string defaultApiUrl, string hint)
    {
        Key = key; Label = label; DefaultApiUrl = defaultApiUrl; ApiUrlHint = hint;
    }

    public async Task<GitTestResult> TestAsync(GitConnection c, HttpClient http, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            if (c.AuthMode == "app")
            {
                if (c.AppId.Length == 0 || c.InstallationId.Length == 0 || string.IsNullOrEmpty(c.AppKeyPem))
                    return new(false, new() { "App ID, installation ID and private key are needed." });
                var jwt = AppJwt(c.AppId, c.AppKeyPem!, DateTimeOffset.UtcNow);
                using (var app = await SendAsync(http, HttpMethod.Get, Url(c, "/app"), "Bearer " + jwt, ct))
                {
                    if (!app.IsSuccessStatusCode) return new(false, new() { Explain("app", app.StatusCode) });
                    using var d = JsonDocument.Parse(await app.Content.ReadAsStringAsync(ct));
                    lines.Add("GitHub App: " + Str(d.RootElement, "name") + " (" + Str(d.RootElement, "slug") + ")");
                }
                var token = await InstallationTokenAsync(c, http, ct);
                if (token.Token is null) return new(false, new[] { token.Error! }.Concat(lines).ToList());
                var repos = await ReposWithAsync(c, "token " + token.Token, "/installation/repositories", http, ct);
                lines.Add($"Repositories the app may read: {repos.Count}");
                lines.InsertRange(0, new[] { "Connection works." });
                return new(true, lines);
            }
            if (string.IsNullOrEmpty(c.Token)) return new(false, new() { "Enter a token." });
            using (var me = await SendAsync(http, HttpMethod.Get, Url(c, "/user"), "Bearer " + c.Token, ct))
            {
                if (!me.IsSuccessStatusCode) return new(false, new() { Explain("token", me.StatusCode) });
                using var d = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct));
                lines.Add("Signed in as: " + Str(d.RootElement, "login"));
            }
            var list = await ReposWithAsync(c, "Bearer " + c.Token, "/user/repos?affiliation=owner,collaborator,organization_member", http, ct);
            lines.Add($"Repositories the token may read: {list.Count}");
            lines.Insert(0, "Connection works.");
            return new(true, lines);
        }
        catch (TaskCanceledException) { return new(false, new() { "The git server did not answer in time." }); }
        catch (HttpRequestException) { return new(false, new() { "The git server cannot be reached at this address." }); }
        catch (JsonException) { return new(false, new() { "The address answered, but not like a GitHub API." }); }
        catch (CryptographicException) { return new(false, new() { "The private key cannot be read. Paste the whole .pem file of the GitHub App." }); }
        catch (ArgumentException) { return new(false, new() { "The private key cannot be read. Paste the whole .pem file of the GitHub App." }); }
    }

    public async Task<List<string>> ListReposAsync(GitConnection c, HttpClient http, CancellationToken ct)
    {
        try
        {
            if (c.AuthMode == "app")
            {
                var t = await InstallationTokenAsync(c, http, ct);
                return t.Token is null ? new() : await ReposWithAsync(c, "token " + t.Token, "/installation/repositories", http, ct);
            }
            return string.IsNullOrEmpty(c.Token) ? new()
                : await ReposWithAsync(c, "Bearer " + c.Token, "/user/repos?affiliation=owner,collaborator,organization_member", http, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or CryptographicException or ArgumentException)
        {
            return new();
        }
    }

    public bool VerifyWebhook(IHeaderDictionary headers, byte[] body, string secret, out string evt, out string delivery)
    {
        evt = Clip(headers["X-GitHub-Event"].ToString(), 60);
        delivery = Clip(headers["X-GitHub-Delivery"].ToString(), 80);
        return SignatureValid(headers["X-Hub-Signature-256"].ToString(), body, secret);
    }

    /// <summary>"sha256=&lt;hex&gt;" over the raw body with the webhook secret, compared in constant time.</summary>
    public static bool SignatureValid(string header, byte[] body, string secret)
    {
        if (secret.Length == 0 || !header.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] given;
        try { given = Convert.FromHexString(header[7..]); }
        catch (FormatException) { return false; }
        var mine = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return given.Length == mine.Length && CryptographicOperations.FixedTimeEquals(given, mine);
    }

    /// <summary>JWT of a GitHub App (RS256, issued a minute back against clock skew, valid nine minutes).</summary>
    public static string AppJwt(string appId, string pem, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = appId }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + B64(sig);
    }

    private async Task<(string? Token, string? Error)> InstallationTokenAsync(GitConnection c, HttpClient http, CancellationToken ct)
    {
        if (!long.TryParse(c.InstallationId, out var inst) || inst <= 0) return (null, "The installation ID is a number (shown in the address of the app's installation page).");
        var jwt = AppJwt(c.AppId, c.AppKeyPem ?? "", DateTimeOffset.UtcNow);
        using var r = await SendAsync(http, HttpMethod.Post, Url(c, $"/app/installations/{inst}/access_tokens"), "Bearer " + jwt, ct);
        if (!r.IsSuccessStatusCode) return (null, Explain("installation", r.StatusCode));
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var token = Str(d.RootElement, "token");
        return token.Length > 0 ? (token, null) : (null, "The git server gave no installation token.");
    }

    private static async Task<List<string>> ReposWithAsync(GitConnection c, string auth, string path, HttpClient http, CancellationToken ct)
    {
        var list = new List<string>();
        for (var page = 1; page <= 10; page++)   // at most 1,000 repositories
        {
            var sep = path.Contains('?') ? "&" : "?";
            using var r = await SendAsync(http, HttpMethod.Get, Url(c, $"{path}{sep}per_page=100&page={page}"), auth, ct);
            if (!r.IsSuccessStatusCode) break;
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var arr = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement
                : d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("repositories", out var rr) && rr.ValueKind == JsonValueKind.Array ? rr
                : default;
            if (arr.ValueKind != JsonValueKind.Array) break;
            var n = 0;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                var name = Str(e, "full_name");
                if (name.Length > 0 && name.Length <= 200) list.Add(name);
            }
            if (n < 100) break;
        }
        if (c.Owner.Length > 0) list = list.Where(n => n.StartsWith(c.Owner + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Url(GitConnection c, string path) => c.ApiUrl.TrimEnd('/') + path;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string url, string auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.UserAgent.ParseAdd("AddonStore-Server (git)");
        return await http.SendAsync(req, ct);
    }

    private static string Explain(string what, System.Net.HttpStatusCode code) => ((int)code, what) switch
    {
        (401, "app") => "The git server refused the app: check the App ID and the private key.",
        (401, _) => "The git server refused the sign-in: the token is wrong or has expired.",
        (403, _) => "The git server refused access (403): the token or app lacks rights, or a rate limit applies.",
        (404, "installation") => "The installation was not found: check the installation ID (the app must be installed in the organisation).",
        (404, _) => "Nothing found at this address: check the API address (GitHub Enterprise Server: https://host/api/v3).",
        (>= 300 and < 400, _) => "The address redirects elsewhere; the store does not follow redirects with credentials. Enter the final API address.",
        _ => $"The git server answered with status {(int)code}.",
    };

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Clip(string s, int max) => new string(s.Where(ch => !char.IsControl(ch)).Take(max).ToArray());
}

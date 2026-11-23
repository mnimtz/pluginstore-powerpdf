using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AddonStore.Web.Services.GitScan;

/// <summary>
/// Veracode REST API with HMAC sign-in (S2.8.0), read only. Algorithm as in Veracode's own Java sample
/// (HmacRequestSigner, docs.veracode.com/r/t_configure_java_library): data "id=..&amp;host=..&amp;url=..&amp;method=..",
/// key chain HMAC(secret, nonce) -> HMAC(., timestamp ms) -> HMAC(., "vcode_request_version_1") -> HMAC(., data).
/// </summary>
public static class VeracodeClient
{
    public const string HttpName = "veracode";

    /// <summary>The Authorization header for one request. <paramref name="pathAndQuery"/> includes "?query" when there is one.</summary>
    public static string AuthHeader(string apiId, string apiKeyHex, string host, string pathAndQuery, string method,
                                    long timestampMs, byte[] nonce)
    {
        var data = $"id={apiId}&host={host}&url={pathAndQuery}&method={method}";
        var k1 = HMACSHA256.HashData(Convert.FromHexString(apiKeyHex), nonce);
        var k2 = HMACSHA256.HashData(k1, Encoding.UTF8.GetBytes(timestampMs.ToString(CultureInfo.InvariantCulture)));
        var k3 = HMACSHA256.HashData(k2, Encoding.UTF8.GetBytes("vcode_request_version_1"));
        var sig = Convert.ToHexString(HMACSHA256.HashData(k3, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
        return $"VERACODE-HMAC-SHA-256 id={apiId},ts={timestampMs.ToString(CultureInfo.InvariantCulture)},nonce={Convert.ToHexString(nonce).ToLowerInvariant()},sig={sig}";
    }

    /// <summary>API key secrets are hex strings; anything else cannot sign.</summary>
    public static bool KeyLooksValid(string key) =>
        key.Length is >= 32 and <= 256 && key.Length % 2 == 0 && key.All(Uri.IsHexDigit);

    /// <summary>Sign-in test: lists one application profile. Lines for the page, the first one the result.</summary>
    public static async Task<GitTestResult> TestAsync(HttpClient http, string host, string apiId, string apiKey, string policy, CancellationToken ct)
    {
        if (apiId.Length == 0 || apiKey.Length == 0) return new(false, new() { "Enter the API ID and the API key." });
        if (!KeyLooksValid(apiKey)) return new(false, new() { "The API key is a hex string (as Veracode shows it when it is created)." });
        const string path = "/appsec/v1/applications?size=1";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{path}");
            req.Headers.TryAddWithoutValidation("Authorization",
                AuthHeader(apiId, apiKey, host, path, "GET", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomNumberGenerator.GetBytes(16)));
            req.Headers.UserAgent.ParseAdd("AddonStore-Server (veracode)");
            using var r = await http.SendAsync(req, ct);
            var code = (int)r.StatusCode;
            if (code is 401) return new(false, new() { "Veracode refused the sign-in: API ID or key is wrong, expired, or belongs to another region." });
            if (code is 403) return new(false, new() { "Signed in, but the account may not read application profiles (role: Results API or Reviewer)." });
            if (!r.IsSuccessStatusCode) return new(false, new() { $"Veracode answered with status {code}." });
            var lines = new List<string> { "Connection works." };
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object &&
                page.TryGetProperty("total_elements", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var n))
                lines.Add($"Application profiles visible: {n}");
            if (policy.Length > 0) lines.Add("Expected policy: " + policy);
            return new(true, lines);
        }
        catch (TaskCanceledException) { return new(false, new() { "Veracode did not answer in time." }); }
        catch (HttpRequestException) { return new(false, new() { "Veracode cannot be reached from the server." }); }
        catch (JsonException) { return new(false, new() { "Veracode answered with something unexpected." }); }
    }
}

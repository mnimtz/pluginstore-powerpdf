using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AddonStore.Web.Services.GitScan;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.8.0 "Git &amp; Veracode": request signing, webhook signature, app JWT, input rules.</summary>
public class GitScanTests
{
    [Fact]
    public void VeracodeHeaderMatchesTheReferenceAlgorithm()
    {
        // expected value computed independently (Python hmac) after Veracode's Java sample HmacRequestSigner
        var header = VeracodeClient.AuthHeader("abc123", "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
            "api.veracode.com", "/appsec/v1/applications?size=1", "GET", 1700000000000,
            Convert.FromHexString("0102030405060708090a0b0c0d0e0f10"));
        Assert.Equal("VERACODE-HMAC-SHA-256 id=abc123,ts=1700000000000,nonce=0102030405060708090a0b0c0d0e0f10," +
                     "sig=b4701f10d98c8822fa48980c1edf8ce5e28e4d030fbc7a8c60a68760472740c7", header);
    }

    [Theory]
    [InlineData("00112233445566778899aabbccddeeff", true)]
    [InlineData("00112233445566778899aabbccddeef", false)]     // odd length
    [InlineData("00112233445566778899aabbccddeegg", false)]    // not hex
    [InlineData("0011", false)]                                // too short
    public void VeracodeKeyMustBeHex(string key, bool ok) => Assert.Equal(ok, VeracodeClient.KeyLooksValid(key));

    [Fact]
    public void WebhookSignature()
    {
        var body = Encoding.UTF8.GetBytes("{\"zen\":\"x\"}");
        const string good = "sha256=71a39b44c513c2cf45579a36a13e609d0922c87a6f69d5fa27dca3c9df8b6217";
        Assert.True(GitHubProvider.SignatureValid(good, body, "topsecret"));
        Assert.True(GitHubProvider.SignatureValid(good.ToUpperInvariant().Replace("SHA256=", "sha256="), body, "topsecret"));
        Assert.False(GitHubProvider.SignatureValid(good, body, "other"));
        Assert.False(GitHubProvider.SignatureValid(good, Encoding.UTF8.GetBytes("{\"zen\":\"y\"}"), "topsecret"));
        Assert.False(GitHubProvider.SignatureValid(good[7..], body, "topsecret"));          // without "sha256="
        Assert.False(GitHubProvider.SignatureValid("sha256=zz", body, "topsecret"));
        Assert.False(GitHubProvider.SignatureValid("sha256=71a3", body, "topsecret"));       // too short
        Assert.False(GitHubProvider.SignatureValid(good, body, ""));                         // no secret, never valid
    }

    [Fact]
    public void AppJwtIsSignedAndShortLived()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = GitHubProvider.AppJwt("12345", pem, now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var payload = JsonDocument.Parse(B64(parts[1]));
        Assert.Equal("12345", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(1_800_000_000 - 60, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(1_800_000_000 + 540, payload.RootElement.GetProperty("exp").GetInt64());   // GitHub allows at most 10 minutes
        using var header = JsonDocument.Parse(B64(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
    }

    [Theory]
    [InlineData("https://api.github.com", true)]
    [InlineData("https://git.example.com/api/v3", true)]
    [InlineData("http://localhost:18780", true)]
    [InlineData("http://127.0.0.1:9", true)]
    [InlineData("http://git.example.com/api/v3", false)]
    [InlineData("https://user:pw@api.github.com", false)]
    [InlineData("https://api.github.com/#x", false)]
    [InlineData("ftp://api.github.com", false)]
    [InlineData("api.github.com", false)]
    public void ApiAddress(string url, bool ok) => Assert.Equal(ok, GitScanConfig.ApiUrlAllowed(url, out _));

    [Theory]
    [InlineData("30, 7,1", "30,7,1")]
    [InlineData("1,7,30,7", "30,7,1")]
    [InlineData("x,0,400,-3", "")]
    [InlineData("", "")]
    [InlineData("1,2,3,4,5,6,7,8", "8,7,6,5,4,3")]
    public void Reminders(string input, string expected) => Assert.Equal(expected, GitScanConfig.NormalizeReminders(input));

    [Theory]
    [InlineData("tungsten-automation/measuretools", true)]
    [InlineData("mnimtz/pluginstore-powerpdf", true)]
    [InlineData("owner/repo.name_1", true)]
    [InlineData("owner", false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("-owner/repo", false)]
    [InlineData("owner/re po", false)]
    [InlineData("https://github.com/owner/repo", false)]
    public void RepoNames(string repo, bool ok) => Assert.Equal(ok, GitScanService.RepoPattern.IsMatch(repo));

    [Theory]
    [InlineData("v*", true)]
    [InlineData("measuretools-v*", true)]
    [InlineData("release/1.*", true)]
    [InlineData("v* ", false)]
    [InlineData("v$(x)", false)]
    public void TagPatterns(string tag, bool ok) => Assert.Equal(ok, GitScanService.TagPatternRule.IsMatch(tag));

    [Theory]
    [InlineData(".github/workflows/addon-release.yml", true)]
    [InlineData(".github/workflows/veracode.yaml", true)]
    [InlineData(".github/workflows/../x.yml", false)]
    [InlineData("workflows/x.yml", false)]
    [InlineData(".github/workflows/x.sh", false)]
    public void WorkflowFiles(string wf, bool ok) => Assert.Equal(ok, GitScanService.WorkflowRule.IsMatch(wf));

    [Theory]
    [InlineData("POST", "/api/packages/com.tungsten.smartbookmarks/suspend", "", true)]
    [InlineData("POST", "/Admin/GitScan", "?handler=Suspend", true)]
    [InlineData("POST", "/Admin/GitScan", "?handler=Resume", false)]          // putting back waits for the end of maintenance
    [InlineData("DELETE", "/api/packages/com.tungsten.smartbookmarks/suspend", "", false)]
    [InlineData("POST", "/api/packages/com.tungsten.smartbookmarks/rollback", "", false)]
    [InlineData("POST", "/api/packages/a/b/suspend", "", false)]
    public void EmergencyBrakeInMaintenance(string method, string path, string query, bool open)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = method; ctx.Request.Path = path; ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
        Assert.Equal(open, AddonStore.Web.Services.MaintenanceGate.IsEmergencyBrake(ctx.Request));
    }

    [Fact]
    public void WebhookIsOpenInMaintenance() =>
        Assert.True(AddonStore.Web.Services.MaintenanceGate.IsOpen(new Microsoft.AspNetCore.Http.PathString("/api/git/webhook")));

    [Theory]
    [InlineData("measuretools", true)]
    [InlineData("store/MeasureTools", true)]
    [InlineData("/abs", false)]
    [InlineData("a//b", false)]
    [InlineData("a/b c", false)]
    public void Folders(string path, bool ok) => Assert.Equal(ok, GitScanService.PathRule.IsMatch(path));
}

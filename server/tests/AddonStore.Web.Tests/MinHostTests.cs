using System.IO.Compression;
using System.Text.Json;
using AddonStore.Web.Data;
using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.5.0: the admin's minimum Power PDF version wins over each version's own and reaches the manual ZIP.</summary>
public class MinHostTests
{
    [Theory]
    [InlineData("2025.3", true)]
    [InlineData("2025.3.7", true)]
    [InlineData("2025.3.7.0", true)]
    [InlineData("5.0", true)]
    [InlineData("2025.3.7.0.1", false)]
    [InlineData("2025.", false)]
    [InlineData("v2025.3", false)]
    [InlineData("2025.3\n", false)]
    [InlineData("<b>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Version_shape(string? value, bool valid) => Assert.Equal(valid, MinHost.IsValid(value));

    [Fact]
    public void Admin_value_wins_else_the_versions_own()
    {
        var v = new PackageVersion { MinPowerPdfVersion = "2025.3" };
        Assert.Equal("2025.3", MinHost.Effective(new Package(), v));
        Assert.Equal("2025.4", MinHost.Effective(new Package { MinPowerPdfOverride = "2025.4" }, v));
        Assert.Equal("2025.3", MinHost.Effective(new Package { MinPowerPdfOverride = "  " }, v));
        Assert.Equal("2025.4", MinHost.Effective(new Package { MinPowerPdfOverride = "2025.4" }, null));
        Assert.Equal("", MinHost.Effective(null, null));
    }

    [Fact]
    public void Manual_zip_carries_the_store_minimum_and_rejects_junk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "minhost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ppak = Path.Combine(dir, "p.ppak");
            using (var z = ZipFile.Open(ppak, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open())) w.Write("{\"files\":{\"x64\":\"x64/Demo.zxt\"}}");
                using (var w = new StreamWriter(z.CreateEntry("x64/Demo.zxt").Open())) w.Write("MZ");
            }
            var v = new PackageVersion { PackageId = "com.example.demo", Version = "1.0.0", Sha256 = new string('a', 64),
                                         ManifestJson = "{\"files\":{\"x64\":\"x64/Demo.zxt\"}}" };
            string PackageJson(string minHost)
            {
                using var ms = new MemoryStream();
                Assert.True(ManualInstall.Build(ms, v, ppak, "Demo", "Author", "sig", "https://example.com/a/demo", minHost));
                ms.Position = 0;
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.Entries.Single(e => e.FullName.EndsWith("/package.json"));
                using var r = new StreamReader(entry.Open());
                return JsonDocument.Parse(r.ReadToEnd()).RootElement.GetProperty("minPowerPdfVersion").GetString() ?? "";
            }
            Assert.Equal("2025.4", PackageJson("2025.4"));
            Assert.Equal("", PackageJson(""));
            Assert.Equal("", PackageJson("2025.4; rm -rf"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

using System.IO.Compression;
using System.Text;
using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>
/// License files in the source scan (THIRDPARTY_SOURCE_DETECTED, LICENSE_COPYLEFT_SOURCE): only
/// LICENSE/LICENCE/COPYING/NOTICE texts count, never source files. Found with Kennwortsafe 0.1.0
/// (Nov 10, 2026), whose own module notice.cpp/notice.h reported "addon/src (license file)".
/// </summary>
public class LicenseFileTests
{
    [Theory]
    [InlineData("NOTICE")]
    [InlineData("LICENSE")]
    [InlineData("LICENCE")]
    [InlineData("COPYING")]
    [InlineData("LICENSE.txt")]
    [InlineData("LICENSE.md")]
    [InlineData("NOTICE.txt")]
    [InlineData("LICENSE-MIT")]
    [InlineData("LICENSE-MIT.txt")]
    [InlineData("LICENSE_APACHE.rst")]
    [InlineData("COPYING.LESSER")]
    [InlineData("COPYING.LIB")]
    [InlineData("third_party/zxing/LICENSE")]
    [InlineData(@"third_party\zxing\NOTICE.md")]
    public void License_texts_are_license_files(string path)
    {
        Assert.True(SourceService.IsLicenseFile(path));
    }

    [Theory]
    [InlineData("notice.cpp")]
    [InlineData("notice.h")]
    [InlineData("addon/src/notice.cpp")]
    [InlineData("license.c")]
    [InlineData("license.hpp")]
    [InlineData("LicenseCheck.cs")]
    [InlineData("license_check.js")]
    [InlineData("copying.py")]
    [InlineData("notice.json")]
    [InlineData("license.png")]
    [InlineData("LICENSES.md")]
    [InlineData("licensed.txt")]
    [InlineData("noticeboard.txt")]
    [InlineData("license-.txt")]
    public void Other_files_are_not_license_files(string path)
    {
        Assert.False(SourceService.IsLicenseFile(path));
    }

    [Fact]
    public void Notice_does_not_mark_a_third_party_folder_for_the_security_scan()
    {
        Assert.False(SourceService.IsLicenseFile("lib/NOTICE", includeNotice: false));
        Assert.True(SourceService.IsLicenseFile("lib/LICENSE", includeNotice: false));
    }

    [Fact]
    public void Own_notice_module_is_not_reported_as_third_party_code()
    {
        var report = CheckZip(
            ("addon/src/main.cpp", "int main() { return 0; }"),
            ("addon/src/notice.cpp", "// shows a notice\nvoid ShowNotice() {}"),
            ("addon/src/notice.h", "#pragma once\nvoid ShowNotice();"));

        Assert.DoesNotContain(report.Findings, f => f.Code == "THIRDPARTY_SOURCE_DETECTED");
    }

    [Fact]
    public void Vendored_license_text_is_still_reported()
    {
        var report = CheckZip(
            ("addon/src/main.cpp", "int main() { return 0; }"),
            ("addon/third_party/lib/LICENSE-MIT", "Permission is hereby granted, free of charge, to any person obtaining a copy"),
            ("addon/third_party/lib/NOTICE", "This product includes software developed elsewhere."));

        var f = Assert.Single(report.Findings, x => x.Code == "THIRDPARTY_SOURCE_DETECTED");
        Assert.Contains("addon/third_party/lib (MIT)", f.Message);
    }

    private static AddonStore.Web.Validation.ValidationReport CheckZip(params (string Name, string Text)[] files)
    {
        var path = Path.Combine(Path.GetTempPath(), $"license-test-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var (name, text) in files)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                    w.Write(text);
                }
            return SourceService.Check(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

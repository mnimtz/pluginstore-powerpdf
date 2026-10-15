using System.Text;
using AddonStore.Web.Validation;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>
/// Import check of the .zxt (FOREIGN_DEPENDENCY): DLLs that ship with Windows pass,
/// everything else is reported. Found with ZoneTools 1.0.0 (Oct 15, 2026), whose
/// Uniscribe import USP10.dll was flagged although it lives in System32.
/// </summary>
public class ImportCheckTests
{
    [Fact]
    public void Zxt_importing_usp10_has_no_foreign_dependency()
    {
        var report = Check("KERNEL32.dll", "USP10.dll", "USER32.dll");

        Assert.DoesNotContain(report.Findings, f => f.Code == "FOREIGN_DEPENDENCY");
    }

    [Theory]
    [InlineData("usp10.dll")]
    [InlineData("shcore.dll")]
    [InlineData("msftedit.dll")]
    [InlineData("riched20.dll")]
    [InlineData("t2embed.dll")]
    [InlineData("fontsub.dll")]
    [InlineData("d3d11.dll")]
    [InlineData("dxgi.dll")]
    [InlineData("dcomp.dll")]
    [InlineData("dwrite.dll")]
    [InlineData("d2d1.dll")]
    [InlineData("gdiplus.dll")]
    [InlineData("windowscodecs.dll")]
    [InlineData("uxtheme.dll")]
    [InlineData("dwmapi.dll")]
    [InlineData("msimg32.dll")]
    public void Windows_text_and_graphics_dlls_pass(string dll)
    {
        var report = Check("KERNEL32.dll", dll);

        Assert.DoesNotContain(report.Findings, f => f.Code == "FOREIGN_DEPENDENCY");
    }

    [Fact]
    public void Dll_outside_windows_is_still_reported()
    {
        var report = Check("KERNEL32.dll", "USP10.dll", "libharfbuzz-0.dll");

        var finding = Assert.Single(report.Findings, f => f.Code == "FOREIGN_DEPENDENCY");
        Assert.Contains("libharfbuzz-0.dll", finding.Message);
        Assert.DoesNotContain("USP10.dll", finding.Message);
    }

    private static ValidationReport Check(params string[] imports)
    {
        var report = new ValidationReport();
        PackageValidator.CheckPe(report, "x64", "x64/Test.zxt", BuildDll(imports), "1.0.0");
        return report;
    }

    /// <summary>
    /// Smallest PE32+ x64 DLL the validator walks: one section that holds the import
    /// descriptors (zero-terminated) followed by the DLL names.
    /// </summary>
    private static byte[] BuildDll(string[] imports)
    {
        const int PeOffset = 0x40, OptSize = 0xF0, RawOffset = 0x200, SectionVa = 0x1000, SectionSize = 0x400;
        var bytes = new byte[RawOffset + SectionSize];
        void U16(int at, int v) => BitConverter.GetBytes((ushort)v).CopyTo(bytes, at);
        void U32(int at, long v) => BitConverter.GetBytes((uint)v).CopyTo(bytes, at);

        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        U32(0x3C, PeOffset);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(bytes, PeOffset);
        U16(PeOffset + 4, 0x8664);                 // machine x64
        U16(PeOffset + 6, 1);                      // one section
        U16(PeOffset + 20, OptSize);
        U16(PeOffset + 22, 0x2000 | 0x0002 | 0x0020);   // DLL, executable, large address aware

        var opt = PeOffset + 24;
        U16(opt, 0x20B);                           // PE32+
        U16(opt + 70, 0x0040 | 0x0100 | 0x0020);   // DYNAMIC_BASE, NX_COMPAT, HIGH_ENTROPY_VA
        U32(opt + 108, 16);                        // number of data directories
        var descriptorsSize = (imports.Length + 1) * 20;
        U32(opt + 120, SectionVa);                 // import directory
        U32(opt + 124, descriptorsSize);

        var sec = opt + OptSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(bytes, sec);
        U32(sec + 8, SectionSize);
        U32(sec + 12, SectionVa);
        U32(sec + 16, SectionSize);
        U32(sec + 20, RawOffset);
        U32(sec + 36, 0xC0000040);                 // initialized data, read, write

        var name = descriptorsSize;
        for (int i = 0; i < imports.Length; i++)
        {
            U32(RawOffset + i * 20 + 12, SectionVa + name);
            Encoding.ASCII.GetBytes(imports[i]).CopyTo(bytes, RawOffset + name);
            name += imports[i].Length + 1;
        }
        return bytes;
    }
}

using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.0.1: the catalog entry keeps canonical language codes (it lowercased "zh-Hans" and then refused it).</summary>
public class CatalogEntryLanguageTests
{
    [Theory]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-hans", "zh-Hans")]
    [InlineData("ZH-HANT", "zh-Hant")]
    [InlineData(" de ", "de")]
    [InlineData("EN", "en")]
    [InlineData("no", "no")]
    [InlineData("xx", "xx")]
    public void Codes_are_canonical(string given, string expected) =>
        Assert.Equal(expected, PackageMetaService.CanonicalLang(given));

    [Fact]
    public void Description_with_chinese_keeps_the_codes()
    {
        var c = new MetaChange { SetDescription = true, Description = new() { ["en"] = "Text", ["zh-Hans"] = "\u6587\u672c", ["zh-hant"] = "\u6587\u672c" } };
        PackageMetaService.Normalize(c);
        Assert.NotNull(c.Description);
        Assert.Contains("zh-Hans", c.Description!.Keys);
        Assert.Contains("zh-Hant", c.Description!.Keys);
        Assert.DoesNotContain("zh-hans", c.Description!.Keys);
    }
}

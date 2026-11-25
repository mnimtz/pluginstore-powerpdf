using AddonStore.Web.Services;
using Xunit;

namespace AddonStore.Web.Tests;

/// <summary>S2.8.6: page names of the customers' own catalog pages (/finance).</summary>
public class CustomerPageTests
{
    [Theory]
    [InlineData("finance", true)]
    [InlineData("finance-2026", true)]
    [InlineData("hr", true)]
    [InlineData("f", false)]                 // too short
    [InlineData("Finance", false)]           // stored and checked in lower case (the page lower-cases the address)
    [InlineData("-finance", false)]
    [InlineData("finance-", false)]
    [InlineData("fin ance", false)]
    [InlineData("finanzämter", false)]
    [InlineData("a234567890123456789012345678901234567890", true)]    // 40: the longest
    [InlineData("a2345678901234567890123456789012345678901", false)]  // 41
    [InlineData("admin", false)]             // the store's own addresses
    [InlineData("api", false)]
    [InlineData("a", false)]
    [InlineData("docs", false)]
    [InlineData("customer-logo", false)]
    [InlineData("customer-banner", false)]   // S2.8.7
    [InlineData("download", false)]
    public void Slugs(string slug, bool ok) => Assert.Equal(ok, CustomerPageService.CheckSlug(slug) is null);

    [Theory]
    [InlineData("FINANCE", "AddonStore-Code-FINANCE.msi")]
    [InlineData("finance", "AddonStore-Code-FINANCE.msi")]
    [InlineData("Finance-2026", "AddonStore-Code-FINANCE2026.msi")]
    [InlineData("", "AddonStore-2.4.0.msi")]
    [InlineData(null, "AddonStore-2.4.0.msi")]
    [InlineData("FIN", "AddonStore-2.4.0.msi")]                 // not a code of your own
    [InlineData("../../evil", "AddonStore-2.4.0.msi")]
    [InlineData("FINANCE\"x", "AddonStore-2.4.0.msi")]
    [InlineData("FINANZÄMTER", "AddonStore-2.4.0.msi")]
    public void InstallerName(string? code, string name) => Assert.Equal(name, CustomerPageService.InstallerName(code, "2.4.0"));   // S2.8.8

    [Fact]
    public void PictureLimits()
    {
        // S2.8.7: the logo stays at 1 MB, a banner photo may have 2 MB (the brand photos have up to about 1.2 MB)
        Assert.Equal(1024 * 1024, CustomerPageService.MaxBytes(CustomerPageService.Picture.Logo));
        Assert.Equal(2 * 1024 * 1024, CustomerPageService.MaxBytes(CustomerPageService.Picture.Banner));
        Assert.Contains("1 MB", CustomerPageService.RuleText(CustomerPageService.Picture.Logo));
        Assert.Contains("2 MB", CustomerPageService.RuleText(CustomerPageService.Picture.Banner));
    }
}
